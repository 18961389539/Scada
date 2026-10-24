using System.Text.Json.Serialization;

namespace Scada.Core.Entities;

public sealed class GatewayControlCommand
{
    public Guid Id { get; set; }
    public int GatewayId { get; set; }
    public Guid StreamId { get; set; }
    [JsonIgnore] public Guid SessionId { get; set; }
    public int DeviceId { get; set; }
    public int TagId { get; set; }
    public double Value { get; set; }
    public string Revision { get; set; } = "";
    public long CreatedTs { get; set; }
    public long ExpiresTs { get; set; }
    public string State { get; set; } = "Queued";
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    [JsonIgnore] public string? ProtectedExecutionToken { get; set; }
    [JsonIgnore] public string? ExecutionTokenHash { get; set; }
    public string? ResultJson { get; set; }
    [JsonIgnore] public string? ReceiptHash { get; set; }
    public long? CompletedTs { get; set; }
}
