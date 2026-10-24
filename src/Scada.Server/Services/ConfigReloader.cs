using Scada.Core.Interfaces;
using Scada.Data;

namespace Scada.Server.Services;

public sealed class ConfigReloader
{
    private readonly IConfigSnapshot _snapshot;
    private readonly ICollectorControl _collector;
    private readonly AccessModelProvider? _access;

    public ConfigReloader(IConfigSnapshot snapshot, ICollectorControl collector, AccessModelProvider? access = null)
    {
        _snapshot = snapshot;
        _collector = collector;
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
        _collector.RequestReload();
    }
}
