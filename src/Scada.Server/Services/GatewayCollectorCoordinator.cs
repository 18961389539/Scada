using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Server.Services;

public sealed class GatewayCollectorCoordinator(CollectorEngine local, GatewayCenterService gateways, IConfigSnapshot config) : ICollectorControl
{
    public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => local.GetStatuses().Concat(gateways.GetStatuses()).OrderBy(x => x.DeviceId).ToList();
    public void RequestReload() => local.RequestReload();
    public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default) =>
        config.TryGetDevice(deviceId, out var device) && device.GatewayId != null
            ? Task.FromResult(new CommandExecutionResult(false, "远程命令缺少可审计的操作账户，命令未下发", Outcome: GatewayCommandOutcome.NotSent))
            : local.WriteAsync(deviceId, tag, value, cancellationToken);
    public Task<CommandExecutionResult> WriteIdentifiedAsync(int deviceId, Tag tag, double value, CollectorCommandContext context, CancellationToken ct = default) =>
        config.TryGetDevice(deviceId, out var device) && device.GatewayId is not null
            ? gateways.ExecuteCommandAsync(deviceId, tag.Id, value, context, ct)
            : local.WriteAsync(deviceId, tag, value, ct);
}
