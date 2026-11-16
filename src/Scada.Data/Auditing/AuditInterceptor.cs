using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;

namespace Scada.Data.Auditing;

/// <summary>
/// 把「实体增删改」自动落成审计行：实体实现 <see cref="IAuditedEntity"/> 即可，
/// 不再要求每个写入口手写 <c>OperationLogs.Add</c>——漏写一条就少一条审计，
/// 而这种漏写在两百个调用点里几乎不可能靠 review 守住。
///
/// 审计表在 runtime.db，实体写入在 config.db，两个文件无法同事务提交。此前的做法是
/// "业务提交后再开一个上下文写运行库"：进程在两次写之间崩溃，配置已经生效而审计永久缺失；
/// 运行库被报警写入锁住时，配置保存还要陪着等 30 秒。
/// 现在改成事务性发件箱：审计行与业务写入同一个事务落到配置库的 AuditOutbox，
/// 提交之后再尽力投递到运行库（短超时），投不动的留给后台服务重试。
/// 代价是创建操作的主键要在保存后回填一次（见 <see cref="PatchLateIdsAsync"/>）。
/// </summary>
public sealed class AuditInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private static readonly ConditionalWeakTable<DbContext, PendingState> Pending = new();
    // 中文直接落库（不转 \uXXXX）：发件箱里的 JSON 要能被人直接读，也省一半体积。
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class PendingState
    {
        public List<PendingItem> Items { get; } = [];
        public void Reset() => Items.Clear();
    }

    private sealed record PendingItem(AuditOutboxEntry Row, OperationLog Log, Func<string?>? LateId);

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

    // 同步保存可能跑在带 SynchronizationContext 的线程上（WPF、Blazor Server），
    // 在这里同步等待异步投递会死锁。主键回填仍用同一个上下文完成（不额外开会话），
    // 投递交给后台任务补做。
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context && Pending.TryGetValue(context, out var state))
        {
            Pending.Remove(context);
            if (state.Items.Count > 0 && PatchLateIds(context, state)) context.SaveChanges();
        }
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not { } context || !Pending.TryGetValue(context, out var state))
            return new ValueTask<int>(result);
        Pending.Remove(context);
        if (state.Items.Count == 0) return new ValueTask<int>(result);

        var flush = CompleteAsync(context, state, cancellationToken);
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
        if (context is not ConfigDbContext db || AuditContext.Suppressed) return;

        var entries = context.ChangeTracker.Entries()
            .Where(x => x.Entity is IAuditedEntity
                && x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && (x.State != EntityState.Modified || x.Properties.Any(p => p.IsModified)))
            .ToArray();
        if (entries.Length == 0) return;

        var actor = AuditContext.Current;
        var now = ScadaTime.NowMs();
        var state = Pending.GetOrCreateValue(context);
        state.Reset();
        var outbox = db.Set<AuditOutboxEntry>();
        foreach (var entry in entries)
        {
            var entity = (IAuditedEntity)entry.Entity;
            var verb = entry.State switch
            {
                EntityState.Added => "创建",
                EntityState.Modified => "修改",
                _ => "删除"
            };
            // 新增的主键要等 INSERT 之后才知道，先留一个延后取值的钩子；其余状态在保存前就能取。
            var lateId = entry.State == EntityState.Added
                ? (Func<string?>?)(() => entry.Property("Id").CurrentValue?.ToString())
                : null;
            var log = new OperationLog
            {
                UserId = actor.UserId,
                UserName = actor.UserName,
                Ts = now,
                Action = Truncate($"{verb}{entity.AuditLabel}：{entity.AuditName}", 200),
                TargetType = entity.AuditTargetType,
                TargetId = lateId is null ? entry.Property("Id").CurrentValue?.ToString() : null,
                Details = AuditDiff.Compute(
                    entry.State == EntityState.Added ? null : entry.OriginalValues.ToObject(),
                    entry.State == EntityState.Deleted ? null : entry.Entity),
                Result = "成功"
            };
            var row = new AuditOutboxEntry
            {
                CreatedTs = now,
                Payload = JsonSerializer.Serialize(log, PayloadJson),
                NextAttemptTs = now
            };
            outbox.Add(row);
            state.Items.Add(new PendingItem(row, log, lateId));
        }
    }

    private async Task CompleteAsync(DbContext context, PendingState state, CancellationToken cancellationToken)
    {
        try
        {
            // 回填必须走同一个 DbContext：端点自己开事务时，另开连接会等配置库的写锁（最长 5 秒）。
            if (PatchLateIds(context, state)) await context.SaveChangesAsync(cancellationToken);
            var drainer = services.GetService<AuditOutboxDrainer>();
            // 调用方自己开了事务时不能在 SavedChanges 阶段另开配置库连接：连接初始化会执行
            // PRAGMA journal_mode / auto_vacuum，写事务未提交时这些语句要抢库级锁，
            // 而拦截器里的原生 DbCommand 不受 EF 的 SetCommandTimeout 约束，会一直等到
            // 默认的 30 秒命令超时，把一次配置保存拖成 HTTP 504。发件箱行已经随业务事务
            // 落库，提交后由后台补投即可（SourceId 保证不会重复）。
            if (drainer is not null && context.Database.CurrentTransaction is null)
                // 内联投递不删行（删行要抢配置库写锁），因此积压会累积；每次都按整批上限投，
                // 否则新写的审计会被前面留下的旧行挤出 maxRows，表现为"这一条没审计"。
                await drainer.TryDrainAsync(AuditOutboxDrainer.BatchSize, AuditOutboxDrainer.InlineTimeoutSeconds, cancellationToken);
        }
        catch (Exception ex)
        {
            // 审计是旁路：发件箱行已经随业务提交，投递失败只影响时效，不能让业务调用失败。
            services.GetService<ILoggerFactory>()?.CreateLogger<AuditInterceptor>()
                .LogWarning(ex, "审计投递失败（{Count} 条，操作者 {Actor}），已留在发件箱等待重试。", state.Items.Count, AuditContext.Current.UserName);
        }
    }

    /// <summary>
    /// 新增实体的主键保存后才有值：把 TargetId 补进发件箱行。
    /// 失败只影响这一条审计的目标编号，审计本身不会丢。
    /// </summary>
    private static bool PatchLateIds(DbContext context, PendingState state)
    {
        var patched = false;
        foreach (var item in state.Items)
        {
            if (item.LateId is null) continue;
            var targetId = item.LateId();
            if (targetId is null) continue;
            item.Log.TargetId = targetId;
            var payload = JsonSerializer.Serialize(item.Log, PayloadJson);
            if (item.Row.Payload == payload) continue;
            item.Row.Payload = payload;
            context.Entry(item.Row).Property(x => x.Payload).IsModified = true;
            patched = true;
        }
        return patched;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
