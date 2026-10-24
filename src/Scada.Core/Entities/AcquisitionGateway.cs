using System.Text.Json.Serialization;
using Scada.Core.Enums;

namespace Scada.Core.Entities;

public sealed class AcquisitionGateway
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public string TokenHash { get; set; } = "";
    [JsonIgnore] public Guid? SessionId { get; set; }
    [JsonIgnore] public Guid? InstanceId { get; set; }
    [JsonIgnore] public Guid? ActiveStreamId { get; set; }
    public long LeaseUntilTs { get; set; }
    public long LastSeenTs { get; set; }
    public string? AgentVersion { get; set; }
    public string CapabilitiesJson { get; set; } = "[]";
    public string? AppliedRevision { get; set; }
    public long PendingBatches { get; set; } = -1;
    public long PendingBytes { get; set; }
    public long? OldestPendingTs { get; set; }
    public string? LastError { get; set; }
}

public sealed class GatewayConfigurationRevision
{
    public int Id { get; set; }
    public int GatewayId { get; set; }
    public string Revision { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public long CreatedTs { get; set; }
}

public sealed class GatewayDataStream
{
    public int GatewayId { get; set; }
    public Guid StreamId { get; set; }
    public long ReceivedThrough { get; set; }
    public string? LastPayloadHash { get; set; }
    public long ProcessedThrough { get; set; }
}

public sealed class GatewayInboxBatch
{
    public int GatewayId { get; set; }
    public Guid StreamId { get; set; }
    public long Sequence { get; set; }
    public Guid SessionId { get; set; }
    public int DeviceId { get; set; }
    public string Revision { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public int PayloadBytes { get; set; }
    public long ReceivedTs { get; set; }
    public long NextAttemptTs { get; set; }
    public string? ProcessingError { get; set; }
}

public sealed class GatewayArchiveCursor
{
    public int GatewayId { get; set; }
    public Guid StreamId { get; set; }
    public int TagId { get; set; }
    public long Ts { get; set; }
    public double Value { get; set; }
    public TagQuality Quality { get; set; }
}
