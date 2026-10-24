using System.Text.Json;
using System.Text.Json.Serialization;
using Scada.Core.Entities;

namespace Scada.Core.Models;

public static class GatewayProtocol
{
    public const int Version = 1;
    public const int MaxValuesPerBatch = 5000;
    public static JsonSerializerOptions Json { get; } = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter()); return json;
    }
    public static bool IsRevision(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed record GatewayDeviceConfiguration(Device Device, string? OpcPassword)
{
    public override string ToString() => $"GatewayDeviceConfiguration {{ DeviceId = {Device.Id}, HasOpcPassword = {OpcPassword is not null} }}";
}
public sealed record GatewayConfiguration(string Revision, IReadOnlyList<GatewayDeviceConfiguration> Devices, IReadOnlyList<Tag> Tags);
public sealed record GatewayTelemetry(int Version, Guid StreamId, long Sequence, int DeviceId, string ConfigurationRevision,
    long CreatedTs, IReadOnlyList<TagValue> Values);
public sealed record GatewayAcknowledgement(Guid StreamId, long Sequence);
public sealed record GatewayCommand(Guid Id, int DeviceId, int TagId, double Value, string ConfigurationRevision, long CreatedTs, long ExpiresTs);
public enum GatewayCommandOutcome { Confirmed, Failed, NotSent, Unknown }
public sealed record GatewayCommandResult(GatewayCommandOutcome Outcome, string Message, double? ReadBack = null);
public sealed record GatewayHello(int Version, Guid InstanceId, Guid StreamId, string AgentVersion);
public sealed record GatewaySession(int Version, Guid SessionId, long ServerTs, long LeaseUntilTs, long ReceivedThrough);
public sealed record GatewayHeartbeat(string? AppliedRevision, long PendingBatches, long PendingBytes, long? OldestPendingTs,
    string? Error, IReadOnlyList<CollectorDeviceStatus>? Devices = null);
public sealed record GatewayCommandOffers(long ServerTs, IReadOnlyList<GatewayCommand> Commands);
public sealed record GatewayCommandGrant(Guid CommandId, Guid SessionId, string ConfigurationRevision, string ExecutionToken, long ServerTs, long ExpiresTs)
{ public override string ToString() => $"GatewayCommandGrant {{ CommandId = {CommandId}, ExpiresTs = {ExpiresTs} }}"; }
public sealed record GatewayCommandReceipt(Guid CommandId, Guid StreamId, string? ExecutionToken, GatewayCommandResult Result)
{ public override string ToString() => $"GatewayCommandReceipt {{ CommandId = {CommandId}, Outcome = {Result.Outcome} }}"; }
public sealed record GatewayCommandReceiptAcknowledgement(Guid CommandId);
public sealed record GatewayCommandStatus(Guid Id, int GatewayId, int DeviceId, int TagId, double Value, string State,
    long CreatedTs, long ExpiresTs, GatewayCommandResult? Result, long? CompletedTs);
