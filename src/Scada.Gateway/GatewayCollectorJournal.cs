using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Gateway;

public sealed class GatewayCollectorJournal : ICollectorJournal
{
    private readonly GatewayAgentStore _store;
    private readonly string _revision;
    public GatewayCollectorJournal(GatewayAgentStore store, string configurationRevision)
    {
        if (!GatewayProtocol.IsRevision(configurationRevision)) throw new ArgumentException("网关配置版本无效", nameof(configurationRevision));
        _store = store; _revision = configurationRevision;
    }
    public async Task AppendAsync(int deviceId, IReadOnlyList<TagValue> values, CancellationToken cancellationToken = default)
    {
        // Bound diagnostic text without discarding the actual measurement or its quality.
        var snapshot = values.Select(value => value.Error?.Length > 1000 ? value with { Error = value.Error[..1000] } : value).ToArray();
        await _store.AppendAsync(deviceId, _revision, snapshot, cancellationToken);
    }
}
