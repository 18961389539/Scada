using Scada.Core.Interfaces;

namespace Scada.Server.Services;

public sealed class ConfigReloader
{
    private readonly IConfigSnapshot _snapshot;
    private readonly ICollectorControl _collector;

    public ConfigReloader(IConfigSnapshot snapshot, ICollectorControl collector)
    {
        _snapshot = snapshot;
        _collector = collector;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _snapshot.ReloadAsync(cancellationToken);
        _collector.RequestReload();
    }
}
