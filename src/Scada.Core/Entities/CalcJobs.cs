namespace Scada.Core.Entities;

public enum CalcJobKind { Accumulator = 0, RunHours = 1, Energy = 2, Script = 3, CSharp = 4 }
public enum AccumulatorMode { Pulse = 0, Flow = 1 }
public enum CalcResetPeriod { None = 0, Shift = 1, Day = 2, Month = 3 }
// 坏质量输入的处理：Hold=沿用最后一个好值（不超过 MaxGapSec）；Skip=该区间不积分；Zero=按 0 计。
public enum BadQualityPolicy { Hold = 0, Skip = 1, Zero = 2 }

// 计算任务：累计器、设备运行小时、能耗积分与受限脚本。输出写入由任务自动维护的派生测点（TagKind.Derived）。
public class CalcJob
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public CalcJobKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public int AreaId { get; set; } = Area.DefaultId;
    public int IntervalMs { get; set; } = 1000;
    public long Revision { get; set; } = 1;
    // 输入测点：累计器为脉冲计数或瞬时流量；运行小时为运行状态；能耗为功率。脚本不用。
    public int? InputTagId { get; set; }
    public AccumulatorMode Mode { get; set; }
    // 脉冲当量或流量/功率的时间单位换算系数（如 m³/h → 每秒积分乘 1/3600）。
    public double Factor { get; set; } = 1;
    // 计数器满量程（如 65535）；为空时计数回落一律视为复位。
    public double? CounterMax { get; set; }
    // 单次脉冲增量上限，超过视为异常跳变而丢弃；为空不限制。
    public double? MaxDelta { get; set; }
    public BadQualityPolicy BadQuality { get; set; } = BadQualityPolicy.Hold;
    public int MaxGapSec { get; set; } = 60;
    // 运行判定阈值：输入值 > 阈值视为运行。
    public double RunThreshold { get; set; } = 0.5;
    // 维护提醒：自上次维护起累计运行小时达到该值即提醒；为空不提醒。
    public double? MaintenanceHours { get; set; }
    public CalcResetPeriod ResetPeriod { get; set; } = CalcResetPeriod.Day;
    // 班次起点（本地时间，逗号分隔 HH:mm，如 "08:00,20:00"）。
    public string ShiftStarts { get; set; } = "08:00,20:00";
    // 分时电价时段 JSON：[{"band":"peak","start":"08:00","end":"11:00"}]，未覆盖时段为 flat。
    public string TouJson { get; set; } = "[]";
    // 受限脚本源码与允许读写的测点白名单（逗号分隔编号）。
    public string Script { get; set; } = string.Empty;
    public string ReadTags { get; set; } = string.Empty;
    public string WriteTags { get; set; } = string.Empty;
    // 脚本写现场测点以该用户身份执行，运行时复核其 Control 权限并记审计。
    public int? RunAsUserId { get; set; }
    public string RunAsUserName { get; set; } = string.Empty;
    public long UpdatedTs { get; set; }
    public bool IsScript => Kind is CalcJobKind.Script or CalcJobKind.CSharp;
}

// 断电续算：任务运行状态定期落盘。
public class CalcJobState
{
    public int JobId { get; set; }
    public long Revision { get; set; }
    public string StateJson { get; set; } = "{}";
    public long UpdatedTs { get; set; }
    public string? LastError { get; set; }
}

// 周期复位前的快照（也用于手动复位、维护确认）。
public class CalcJobSnapshot
{
    public long Id { get; set; }
    public int JobId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string PeriodKey { get; set; } = string.Empty;
    public long PeriodStartTs { get; set; }
    public long Ts { get; set; }
    public string ValuesJson { get; set; } = "{}";
}
