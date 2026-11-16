using Scada.Core.Interfaces;
using Scada.Data;

namespace Scada.Server.Services;

public sealed class ConfigReloader
{
    private readonly IConfigSnapshot _snapshot;
    private readonly ICollectorControl _collector;
    private readonly IRealtimeCache _cache;
    private readonly AccessModelProvider? _access;

    public ConfigReloader(IConfigSnapshot snapshot, ICollectorControl collector, IRealtimeCache cache, AccessModelProvider? access = null)
    {
        _snapshot = snapshot;
        _collector = collector;
        _cache = cache;
        _access = access;
    }

    private long _reloads;
    // 已执行的重载次数（诊断与测试用：例如断言工程导入预览不会触发运行时重载）。
    public long ReloadCount => Interlocked.Read(ref _reloads);

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reloads);
        // 设备/测点归属变化会影响区域授权判定，先让授权模型失效。
        _access?.Invalidate();
        await _snapshot.ReloadAsync(cancellationToken);
        PruneRealtimeCache();
        _collector.RequestReload();
    }

    // 删除测点后缓存条目不会自己消失：留着会让 Snapshot() 与历史快照继续返回已删测点，
    // 现场看到"删了还在跳"。值本身留在各处的实时缓存里也无从回收，只能在这里按配置剔除。
    private void PruneRealtimeCache()
    {
        var live = _snapshot.Tags.Select(x => x.Id).ToHashSet();
        foreach (var tagId in _cache.Snapshot().Keys)
            if (!live.Contains(tagId)) _cache.Remove(tagId);
    }
}
