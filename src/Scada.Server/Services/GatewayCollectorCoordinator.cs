using Microsoft.Extensions.Options;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Acquisition;

namespace Scada.Server.Services;

public sealed class GatewayCollectorCoordinator(ILocalCollector local, GatewayCenterService gateways, IConfigSnapshot config, LocalCommandLedger commands, IOptions<ScadaSettings> settings, IDeviceAcquisitionPaths? paths = null) : ICollectorControl
{
    private readonly IDeviceAcquisitionPaths _paths = paths ?? PrimaryDeviceAcquisitionPaths.Instance;

    public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => local.GetStatuses().Concat(gateways.GetStatuses()).OrderBy(x => x.DeviceId).ToList();
    public void RequestReload() => local.RequestReload();
    public async Task<IReadOnlyList<TagValue>> ReadAsync(int deviceId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
    {
        if (!config.TryGetDevice(deviceId, out var device) || _paths.Resolve(device).GatewayId is null)
        {
            if (device is not null && !settings.Value.LocalCollectionEnabled)
                throw new InvalidOperationException(LocalCollection.DisabledMessage);
            return await local.ReadAsync(deviceId, tagIds, cancellationToken);
        }
        if (!TraceabilityAcquisition.SupportsImmediateRead(device.Protocol))
            throw new NotSupportedException(TraceabilityAcquisition.UnsupportedMessage(device.Id, device.Protocol, device.GatewayId));
        try { return await gateways.ReadCollectedAsync(deviceId, tagIds, cancellationToken); }
        catch (GatewayRequestException ex) { throw new IOException(ex.Message); }
    }
    public async Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!config.TryGetDevice(deviceId, out var device) || _paths.Resolve(device).GatewayId is null)
        {
            if (device is not null && !settings.Value.LocalCollectionEnabled)
                return new(false, LocalCollection.DisabledMessage, Outcome: GatewayCommandOutcome.NotSent);
            return await local.WriteAsync(deviceId, tag, value, cancellationToken);
        }
        if (!TraceabilityAcquisition.SupportsImmediateRead(device.Protocol))
            return new(false, TraceabilityAcquisition.UnsupportedMessage(device.Id, device.Protocol, device.GatewayId), Outcome: GatewayCommandOutcome.NotSent);
        try { return await gateways.WriteCollectedAsync(deviceId, tag.Id, value, cancellationToken); }
        catch (GatewayRequestException ex) { return new(false, ex.Message, Outcome: GatewayCommandOutcome.NotSent); }
    }
    public Task<CommandExecutionResult> WriteIdentifiedAsync(int deviceId, Tag tag, double value, CollectorCommandContext context, CancellationToken ct = default)
    {
        if (config.TryGetDevice(deviceId, out var device) && _paths.Resolve(device).GatewayId is not null)
            return gateways.ExecuteCommandAsync(deviceId, tag.Id, value, context, ct);
        if (device is not null && !settings.Value.LocalCollectionEnabled)
            return Task.FromResult(new CommandExecutionResult(false, LocalCollection.DisabledMessage, Outcome: GatewayCommandOutcome.NotSent));
        return commands.ExecuteAsync(local, deviceId, tag, value, context, ct);
    }
    public Task<CommandExecutionResult> WriteTextAsync(int deviceId, Tag tag, string text, CancellationToken cancellationToken = default)
    {
        if (config.TryGetDevice(deviceId, out var device) && _paths.Resolve(device).GatewayId is not null)
            return Task.FromResult(new CommandExecutionResult(false, "网关设备的字符串写入必须带命令号和操作账户", Outcome: GatewayCommandOutcome.NotSent));
        if (device is not null && !settings.Value.LocalCollectionEnabled)
            return Task.FromResult(new CommandExecutionResult(false, LocalCollection.DisabledMessage, Outcome: GatewayCommandOutcome.NotSent));
        return local.WriteTextAsync(deviceId, tag, text, cancellationToken);
    }
    public Task<CommandExecutionResult> WriteTextIdentifiedAsync(int deviceId, Tag tag, string text, CollectorCommandContext context, CancellationToken ct = default)
    {
        if (config.TryGetDevice(deviceId, out var device) && device.GatewayId is not null)
            return gateways.ExecuteTextCommandAsync(deviceId, tag.Id, text, context, ct);
        if (device is not null && !settings.Value.LocalCollectionEnabled)
            return Task.FromResult(new CommandExecutionResult(false, LocalCollection.DisabledMessage, Outcome: GatewayCommandOutcome.NotSent));
        return local.WriteTextIdentifiedAsync(deviceId, tag, text, context, ct);
    }
}
