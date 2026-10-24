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
            ? Task.FromResult(new CommandExecutionResult(false, "远程命令执行通道尚未贯通，命令未下发"))
            : local.WriteAsync(deviceId, tag, value, cancellationToken);
}
