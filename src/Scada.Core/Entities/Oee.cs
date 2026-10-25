namespace Scada.Core.Entities;

// OEE / 生产统计：产线、产品、班次、停机原因、停机事件、产量修正、分钟/小时/日预聚合。

public enum OeeLossCategory
{
    Availability = 0,
    Performance = 1,
    Quality = 2
}

// 产线运行状态（由状态测点映射，或由停机事件覆盖）。
public enum LineState
{
    Unknown = 0,
    Running = 1,
    Stopped = 2,
    Fault = 3,
    Setup = 4
}

public class ProductionLine
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int AreaId { get; set; } = Area.DefaultId;
    public string? Description { get; set; }
    // 状态测点：数值按 StateMapJson 映射到 LineState；为空时仅靠手动/报警停机事件。
    public int? StateTagId { get; set; }
    // 例如 {"1":"Running","0":"Stopped","2":"Fault","3":"Setup"}；缺省 1=Running、0=Stopped。
    public string StateMapJson { get; set; } = "{}";
    public int? GoodCountTagId { get; set; }
    public int? RejectCountTagId { get; set; }
    // 可选总产量计数器；为空时 Total = Good + Reject + Scrap。
    public int? TotalCountTagId { get; set; }
    // 当前默认产品（IdealCycle 回退到产品，再回退到产线默认）。
    public int? ProductId { get; set; }
    // 默认理想节拍（秒/件）；RatedUnitsPerHour 优先于 IdealCycleSec（若两者都设）。
    public double IdealCycleSec { get; set; } = 60;
    public double? RatedUnitsPerHour { get; set; }
    public double? TargetUnitsPerHour { get; set; }
    public bool Enabled { get; set; } = true;
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}

public class ProductionProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Code { get; set; }
    // null = 全局产品；否则仅该区域可见。
    public int? AreaId { get; set; }
    public double IdealCycleSec { get; set; } = 60;
    public double? RatedUnitsPerHour { get; set; }
    public string? Description { get; set; }
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}

// 生产班次（可跨零点）；与通知值班班次独立。
public class ProductionShift
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int AreaId { get; set; } = Area.DefaultId;
    // 本地时间分钟数（0..1439）；EndMinute <= StartMinute 表示跨零点。
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    // 星期掩码：bit0=周日 ... bit6=周六；按班次开始那天判断。
    public int Days { get; set; } = 127;
    public bool Enabled { get; set; } = true;
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}

public class DowntimeReason
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? AreaId { get; set; }
    public OeeLossCategory Category { get; set; } = OeeLossCategory.Availability;
    // 仅 Availability 有意义：计划停机不计入可用时间分母的损失（从计划生产时间扣除）。
    public bool IsPlanned { get; set; }
    public bool Enabled { get; set; } = true;
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}

public class DowntimeEvent
{
    public long Id { get; set; }
    public int LineId { get; set; }
    public long StartTs { get; set; }
    public long? EndTs { get; set; }
    public int? ReasonId { get; set; }
    public LineState State { get; set; } = LineState.Stopped;
    // tag = 状态测点自动；alarm = 报警候选；manual = 人工录入。
    public string Source { get; set; } = "manual";
    public int? AlarmEventId { get; set; }
    public string? Note { get; set; }
    // 报警候选需操作员确认后才计入 KPI。
    public bool Confirmed { get; set; } = true;
    public string CreatedBy { get; set; } = "";
    public long CreatedTs { get; set; }
    public string? UpdatedBy { get; set; }
    public long? UpdatedTs { get; set; }
}

// 人工产量修正（差分审计）；正数增加、负数冲减；与测点增量一并计入。
public class ProductionCountCorrection
{
    public long Id { get; set; }
    public int LineId { get; set; }
    public int? ProductId { get; set; }
    public long PeriodFrom { get; set; }
    public long PeriodTo { get; set; }
    public double GoodDelta { get; set; }
    public double RejectDelta { get; set; }
    public double ScrapDelta { get; set; }
    public string Reason { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public long CreatedTs { get; set; }
}

// 采集器上次采样快照（每产线一行），用于计算状态区间与计数器增量。
public class OeeLineSample
{
    public int LineId { get; set; }
    public long Ts { get; set; }
    public LineState State { get; set; }
    public double? GoodCount { get; set; }
    public double? RejectCount { get; set; }
    public double? TotalCount { get; set; }
    public int? ProductId { get; set; }
}

// 预聚合：粒度 0=分钟（UTC 整分）、1=小时（UTC 整点）、2=日（ShiftTimeZone 本地零点）。
public class OeeRollup
{
    public const int Minute = 0, Hour = 1, Day = 2;
    public long Id { get; set; }
    public int Granularity { get; set; }
    public long BucketTs { get; set; }
    public int LineId { get; set; }
    public int? ProductId { get; set; }
    public long PlannedMs { get; set; }
    public long RunMs { get; set; }
    public long DownMs { get; set; }
    public long PlannedDownMs { get; set; }
    public long FaultMs { get; set; }
    public long SetupMs { get; set; }
    public double GoodCount { get; set; }
    public double RejectCount { get; set; }
    public double ScrapCount { get; set; }
    // 桶内加权理想节拍（秒/件）；0 表示未知。
    public double IdealCycleSec { get; set; }
    public double TargetCount { get; set; }
}

public class OeeRollupState
{
    public int Id { get; set; } = 1;
    public long? WatermarkTs { get; set; }
    public long? LastRunTs { get; set; }
    public long? RebuildFromTs { get; set; }
    public string? LastError { get; set; }
}

public class OeeConfig
{
    public int Id { get; set; } = 1;
    public string ThresholdsJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}
