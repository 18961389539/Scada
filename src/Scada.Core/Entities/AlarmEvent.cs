using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class AlarmEvent
{
    public int Id { get; set; }
    public int? TagId { get; set; }
    public int? DeviceId { get; set; }
    public int? GatewayId { get; set; }
    public string? EndReason { get; set; }
    public int? RuleId { get; set; }
    public AlarmLevel Level { get; set; }
    public double Value { get; set; }
    public long StartTs { get; set; }
    public long? EndTs { get; set; }
    public string? AckUser { get; set; }
    public long? AckTs { get; set; }
    public string Message { get; set; } = string.Empty;
}
