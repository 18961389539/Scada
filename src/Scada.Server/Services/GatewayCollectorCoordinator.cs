using Scada.Collector;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Server.Services;

public sealed class GatewayCollectorCoordinator(CollectorEngine local, GatewayCenterService gateways, IConfigSnapshot config) : ICollectorControl
{
    public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => local.GetStatuses().Concat(gateways.GetStatuses()).OrderBy(x => x.DeviceId).ToList();
    public void RequestReload() => local.RequestReload();
    public async Task<IReadOnlyList<TagValue>> ReadAsync(int deviceId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
    {
        if (!config.TryGetDevice(deviceId, out var device) || device.GatewayId is null) return await local.ReadAsync(deviceId, tagIds, cancellationToken);
        if (!TraceabilityAcquisition.SupportsImmediateRead(device.Protocol))
            throw new NotSupportedException(TraceabilityAcquisition.UnsupportedMessage(device.Id, device.Protocol, device.GatewayId));
        try { return await gateways.ReadCollectedAsync(deviceId, tagIds, cancellationToken); }
        catch (GatewayRequestException ex) { throw new IOException(ex.Message); }
    }
    public async Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!config.TryGetDevice(deviceId, out var device) || device.GatewayId is null) return await local.WriteAsync(deviceId, tag, value, cancellationToken);
        if (!TraceabilityAcquisition.SupportsImmediateRead(device.Protocol))
            return new(false, TraceabilityAcquisition.UnsupportedMessage(device.Id, device.Protocol, device.GatewayId), Outcome: GatewayCommandOutcome.NotSent);
        try { return await gateways.WriteCollectedAsync(deviceId, tag.Id, value, cancellationToken); }
        catch (GatewayRequestException ex) { return new(false, ex.Message, Outcome: GatewayCommandOutcome.NotSent); }
    }
    public Task<CommandExecutionResult> WriteIdentifiedAsync(int deviceId, Tag tag, double value, CollectorCommandContext context, CancellationToken ct = default) =>
        config.TryGetDevice(deviceId, out var device) && device.GatewayId is not null
            ? gateways.ExecuteCommandAsync(deviceId, tag.Id, value, context, ct)
            : local.WriteAsync(deviceId, tag, value, ct);
}
