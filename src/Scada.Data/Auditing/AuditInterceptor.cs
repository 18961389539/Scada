using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scada.Core.Models;
using Scada.Core.Entities;
using Scada.Core.Security;

namespace Scada.Data.Auditing;

/// <summary>
/// 把「实体增删改」自动落成审计行：实体实现 <see cref="IAuditedEntity"/> 即可，
/// 不再要求每个写入口手写 <c>OperationLogs.Add</c>——漏写一条就少一条审计，
/// 而这种漏写在两百个调用点里几乎不可能靠 review 守住。
///
/// 拦截器挂在 ConfigDbContext 上，但审计表在 runtime.db，所以：
/// 保存前收集变更快照，保存成功后再开一个 RuntimeDbContext 落盘。
/// 审计写失败只记警告，绝不影响已经提交的配置写入（与既有手工审计的取舍一致）。
/// </summary>
public sealed class AuditInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private static readonly ConditionalWeakTable<DbContext, List<PendingAudit>> Pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        FlushAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var flush = FlushAsync(eventData.Context, cancellationToken);
        return flush.IsCompletedSuccessfully
            ? new ValueTask<int>(result)
            : new ValueTask<int>(Await(result, flush));

        static async Task<int> Await(int result, Task flush)
        {
            await flush;
            return result;
        }
    }

    private static void Collect(DbContext? context)
    {
        if (context is null || AuditContext.Suppressed) return;

        var entries = context.ChangeTracker.Entries()
            .Where(x => x.Entity is IAuditedEntity
                && x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && (x.State != EntityState.Modified || x.Properties.Any(p => p.IsModified)))
            .ToList();
        if (entries.Count == 0) return;

        var list = Pending.GetOrCreateValue(context);
        list.Clear();
        foreach (var entry in entries)
        {
            var entity = (IAuditedEntity)entry.Entity;
            var verb = entry.State switch
            {
                EntityState.Added => "创建",
                EntityState.Modified => "修改",
                _ => "删除"
            };
            // 删除后的 entry 会 detach、修改后 OriginalValues 会被覆盖，主键都要在保存前取；
            // 新增的主键只能保存后再读（LateTargetId）。
            var idBeforeSave = entry.State is EntityState.Deleted or EntityState.Modified
                ? entry.Property("Id").CurrentValue?.ToString()
                : null;
            list.Add(new PendingAudit(
                $"{verb}{entity.AuditLabel}：{entity.AuditName}",
                entity.AuditTargetType,
                entry.State == EntityState.Added ? null : idBeforeSave,
                entry.State == EntityState.Added ? null : entry.OriginalValues.ToObject(),
                entry.State == EntityState.Deleted ? null : entry.Entity,
                entry.State == EntityState.Added
                    ? () => entry.Property("Id").CurrentValue?.ToString()
                    : null));
        }
    }

    private async Task FlushAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null || !Pending.TryGetValue(context, out var list)) return;
        Pending.Remove(context);
        if (list.Count == 0) return;

        var actor = AuditContext.Current;
        var now = ScadaTime.NowMs();
        try
        {
            var factory = services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();
            await using var runtime = await factory.CreateDbContextAsync(cancellationToken);
            foreach (var item in list)
            {
                runtime.OperationLogs.Add(new OperationLog
                {
                    UserId = actor.UserId,
                    UserName = actor.UserName,
                    Ts = now,
                    Action = item.Action.Length > 200 ? item.Action[..200] : item.Action,
                    TargetType = item.TargetType,
                    TargetId = item.ResolveTargetId(),
                    Details = AuditDiff.Compute(item.Before, item.After),
                    Result = "成功"
                });
            }
            await runtime.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 审计是旁路：写不进去只能记日志，不能让已经提交的业务写入回滚。
            services.GetService<ILoggerFactory>()?.CreateLogger<AuditInterceptor>()
                .LogWarning(ex, "审计写入失败（{Count} 条，操作者 {Actor}），业务写入保持不变。", list.Count, actor.UserName);
        }
    }

    private sealed record PendingAudit(
        string Action, string TargetType, string? TargetId, object? Before, object? After, Func<string?>? LateTargetId)
    {
        public string? ResolveTargetId() => TargetId ?? LateTargetId?.Invoke();
    }
}
