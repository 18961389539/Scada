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

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        // 设备/测点归属变化会影响区域授权判定，先让授权模型失效。
        _access?.Invalidate();
        await _snapshot.ReloadAsync(cancellationToken);
        _collector.RequestReload();
    }
}
