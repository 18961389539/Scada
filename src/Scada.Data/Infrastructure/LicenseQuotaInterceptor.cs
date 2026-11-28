using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Licensing;

namespace Scada.Data.Infrastructure;

/// <summary>
/// 设备数和测点数在落库前核对授权上限。0 表示不限制。试用和未注册闸门时不拦截。
///
/// 计数来自单例 <see cref="LicenseQuotaCounter"/>，保存成功后再按增量修正：
/// 以前每次 SaveChanges 都新开会话做两次全表 COUNT，批量导入测点时开销会成倍放大。
/// 同步保存路径只读缓存、不做任何 I/O —— 它可能跑在带 SynchronizationContext 的线程上
/// （WPF、Blazor Server），在这里同步等待异步查询会死锁，与 AuditInterceptor 的处理一致。
/// </summary>
public sealed class LicenseQuotaInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private sealed record Delta(int Devices, int Tags);

    // 拦截器实例随 DbContext 重建，SavingChanges → SavedChanges 之间的增量只能挂在 DbContext 上。
    private static readonly ConditionalWeakTable<DbContext, Delta> Deltas = new();

    private LicenseQuotaCounter? Counter => services.GetService<LicenseQuotaCounter>();
    private ILicenseEnforcement? License => services.GetService<ILicenseEnforcement>();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (Enforce(eventData.Context) is { } delta && eventData.Context is { } context) Deltas.AddOrUpdate(context, delta);
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        // 异步保存入口同样只读缓存：保存可能发生在调用方的显式事务里，
        // 在这里新开连接做 COUNT 会等写锁到命令超时（审计 outbox 的 30 秒卡顿就是这么来的）。
        if (Enforce(eventData.Context) is { } delta && eventData.Context is { } context)
            Deltas.AddOrUpdate(context, delta);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    // 同步路径：只读缓存。从未预热时跳过校验（启动路径会预热；授权是商业约束，不是安全边界）。
    // TTL 过期仍用内存旧计数拦住超限，并在后台重算。这里做 I/O 会带来死锁与线程池饥饿。
    private Delta? Enforce(DbContext? context)
    {
        if (Measure(context) is not { } m) return null;
        if (m.AddDevices == 0 && m.AddTags == 0) return m.Delta;
        if (License is not { } license) return m.Delta;
        if (Counter is not { } counter || !counter.TryGetCached(out var devices, out var tags)) return m.Delta;
        if (!counter.IsFresh) counter.RequestRefresh();
        var message = LicenseQuota.Deny(license, devices, tags, m.AddDevices, m.AddTags);
        if (message is not null) throw new LicenseQuotaException(message);
        return m.Delta;
    }

    private static (int AddDevices, int AddTags, Delta Delta)? Measure(DbContext? context)
    {
        if (context is not ConfigDbContext db) return null;
        var addDevices = db.ChangeTracker.Entries<Device>().Count(entry => entry.State == EntityState.Added);
        var addTags = db.ChangeTracker.Entries<Tag>().Count(entry => entry.State == EntityState.Added);
        // 删除会释放额度，即使没有新增也要记增量，否则缓存只涨不落。
        var removedDevices = db.ChangeTracker.Entries<Device>().Count(entry => entry.State == EntityState.Deleted);
        var removedTags = db.ChangeTracker.Entries<Tag>().Count(entry => entry.State == EntityState.Deleted);
        return (addDevices, addTags, new Delta(addDevices - removedDevices, addTags - removedTags));
    }

    private void Flush(DbContext? context)
    {
        if (context is null || !Deltas.TryGetValue(context, out var delta)) return;
        Deltas.Remove(context);
        Counter?.Adjust(delta.Devices, delta.Tags);
    }
}
