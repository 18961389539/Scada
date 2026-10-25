using Scada.Core.Enums;

namespace Scada.Core.Entities;

// 报警统计预聚合：按 (粒度, 桶起点, 报警源, 等级) 汇总开始于该桶的报警。粒度 0=小时（UTC 整点），1=日（ShiftTimeZone 本地零点）。
// 区域过滤在查询时按源的测点/设备推导，不在表中固化（测点区域可能变更）。
public class AlarmRollup
{
    public const int Hour = 0, Day = 1;
    public long Id { get; set; }
    public int Granularity { get; set; }
    public long BucketTs { get; set; }
    public string Source { get; set; } = string.Empty;
    public AlarmLevel Level { get; set; }
    public int? RuleId { get; set; }
    public int? TagId { get; set; }
    public int? DeviceId { get; set; }
    public int? GatewayId { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Count { get; set; }
    public int Annunciated { get; set; }
    public int Shelved { get; set; }
    public int Suppressed { get; set; }
    public int Chattering { get; set; }
    public int Fleeting { get; set; }
    public int AckCount { get; set; }
    public long AckSumMs { get; set; }
    public int ClearCount { get; set; }
    public long ClearSumMs { get; set; }
}

// 预聚合进度：WatermarkTs 之前的整点小时与本地日已汇总（每轮重算最近若干小时以吸收迟到的确认/恢复）。
public class AlarmRollupState
{
    public int Id { get; set; } = 1;
    public long? WatermarkTs { get; set; }
    public long? LastRunTs { get; set; }
    public long? RebuildFromTs { get; set; }
    public string? LastError { get; set; }
}

// 报警分析阈值（单行，JSON）。为空时使用 ScadaSettings 中的默认值。
public class AlarmAnalyticsConfig
{
    public int Id { get; set; } = 1;
    public string ThresholdsJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}
