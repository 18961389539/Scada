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
    // 暂挂/抑制/抖动标记：带标记的报警照常记录，但不发声、不外发。
    public bool Shelved { get; set; }
    public bool Suppressed { get; set; }
    public string? SuppressReason { get; set; }
    public bool Chattering { get; set; }
    // 洪水片段首出：该片段内最早的一条应报警报警，用于快速定位根因。
    public bool FirstOut { get; set; }
}

// ISA-18.2 报警状态：活动未确认/活动已确认/恢复未确认/正常（恢复且已确认）。
public static class AlarmStates
{
    public const string Unack = "Unack";
    public const string Acked = "Acked";
    public const string RtnUnack = "RtnUnack";
    public const string Normal = "Normal";

    public static string Of(AlarmEvent alarm) => alarm.EndTs is null
        ? alarm.AckTs is null ? Unack : Acked
        : alarm.AckTs is null ? RtnUnack : Normal;

    // 是否需要向操作员报警（声光、外发）。
    public static bool Annunciated(AlarmEvent alarm) => !alarm.Shelved && !alarm.Suppressed && !alarm.Chattering;
}
