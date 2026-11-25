using Microsoft.EntityFrameworkCore;

namespace Scada.Data.RuntimeCleanup;

/// <summary>
/// 把 MustClean 清单挂到删除入口：按配置实体名调度已注册钩子。
/// 不清理历史库。OeeRollupState 是全局水位，不按产线删除。
/// </summary>
public static class RuntimeCleanupExecutor
{
    public static void EnsureRegistered() => RuntimeCleanupHooks.Register();

    public static async Task AfterDeletedAsync(RuntimeDbContext db, string configEntity, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configEntity);
        if (ids is not { Count: > 0 }) return;
        RuntimeCleanupHooks.Register();
        foreach (var entry in RuntimeCleanupCatalog.Entries)
        {
            if (entry.Policy != RuntimeCleanupPolicy.MustClean || string.IsNullOrWhiteSpace(entry.Hook)) continue;
            if (!entry.ConfigEntities.Contains(configEntity, StringComparer.Ordinal)) continue;
            if (RuntimeCleanupCatalog.TryGetHook(entry.Hook + ":" + configEntity, out var hook)
                || RuntimeCleanupCatalog.TryGetHook(entry.Hook, out hook))
            {
                await hook(db, ids, cancellationToken);
            }
        }
    }
}

public sealed class RuntimeCleanupRunner(IDbContextFactory<RuntimeDbContext> factory)
{
    public async Task AfterDeletedAsync(string configEntity, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        if (ids is not { Count: > 0 }) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await RuntimeCleanupExecutor.AfterDeletedAsync(db, configEntity, ids, cancellationToken);
    }
}