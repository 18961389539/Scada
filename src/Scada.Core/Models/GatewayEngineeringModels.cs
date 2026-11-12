using System.Text.Json;
using Scada.Core.Enums;

namespace Scada.Core.Models;

public enum GatewayEngineeringOperation { Probe, Diagnostics, DiagnosticsExport, Reconnect, OpcBrowse, OpcClose, OpcMetadata, OpcCertificates, OpcPublicCertificate, OpcTrust, OpcUntrust, ImmediateRead, ImmediateWrite }
public sealed record ImmediateReadRequest(int[] TagIds);
public sealed record ImmediateWriteRequest(int TagId, double Value);
public sealed record DeviceProbeRequest(TagArea? Area = null, int? Address = null, TagDataType? DataType = null, int DbNumber = 1, int BitOffset = 0, string NodeId = "", int TextLength = 0);
public sealed record DeviceProbeResult(bool Connected, string? Area = null, int? Address = null, string? DataType = null, double? Value = null, TagQuality? Quality = null, string? Message = null);
public sealed record GatewayEngineeringRequest(Guid Id, int DeviceId, GatewayEngineeringOperation Operation, JsonElement Payload, string ConfigurationRevision, int UserId, long CreatedTs, long ExpiresTs);
public sealed record GatewayEngineeringOffer(long ServerTs, GatewayEngineeringRequest? Request, Guid? CancelledRequest = null);
public sealed record GatewayEngineeringReply(Guid Id, int StatusCode, JsonElement? Data, string? Error);
public sealed record GatewayEngineeringAcknowledgement(Guid Id);
