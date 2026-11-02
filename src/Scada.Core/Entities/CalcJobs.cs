using Scada.Core.Enums;

namespace Scada.Core.Entities;

public enum CalcJobKind { Accumulator = 0, RunHours = 1, Energy = 2, Script = 3, CSharp = 4 }
public enum CalcTrigger { Periodic = 0, OnChange = 1, OnAlarm = 2, OnStartup = 3 }
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
    // 脚本触发。非脚本任务保持 Periodic。报警边沿：产生、确认、恢复可同时勾选。
    public CalcTrigger Trigger { get; set; } = CalcTrigger.Periodic;
    public double ChangeDeadband { get; set; }
    public int ChangeCooldownMs { get; set; } = 1000;
    public AlarmLevel AlarmMinLevel { get; set; } = AlarmLevel.Info;
    public bool AlarmOnRaise { get; set; } = true;
    public bool AlarmOnAck { get; set; }
    public bool AlarmOnRecover { get; set; }
    // 已发布副本。运行时只执行这里的源码和白名单；Script 等字段是草稿。
    public string PublishedScript { get; set; } = string.Empty;
    public string PublishedReadTags { get; set; } = string.Empty;
    public string PublishedWriteTags { get; set; } = string.Empty;
    public int PublishedHistoryWindowSec { get; set; }
    public bool PublishedAlarmSnapshot { get; set; }
    public long PublishedRevision { get; set; }
    // 触发、周期、死区和报警边沿与脚本一起发布。保存草稿不改变正在运行的副本。
    public CalcTrigger PublishedTrigger { get; set; } = CalcTrigger.Periodic;
    public int PublishedIntervalMs { get; set; } = 1000;
    public double PublishedChangeDeadband { get; set; }
    public int PublishedChangeCooldownMs { get; set; } = 1000;
    public AlarmLevel PublishedAlarmMinLevel { get; set; } = AlarmLevel.Info;
    public bool PublishedAlarmOnRaise { get; set; } = true;
    public bool PublishedAlarmOnAck { get; set; }
    public bool PublishedAlarmOnRecover { get; set; }
    // 0 表示不带历史。开启后只取可读白名单里最多 8 个测点、窗口内最近 60 个点。
    public int HistoryWindowSec { get; set; }
    public bool AlarmSnapshot { get; set; }
    // C# 脚本库名称，逗号分隔。运行时用发布时冻结的源码，不跟随库的后续草稿。
    public string Libraries { get; set; } = string.Empty;
    public string PublishedLibraries { get; set; } = string.Empty;
    public string PublishedLibrarySource { get; set; } = string.Empty;
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

// 已发布脚本的历史。回退把其中一份同时写回草稿和已发布副本。
public class CalcJobPublication
{
    public long Id { get; set; }
    public int JobId { get; set; }
    public long Revision { get; set; }
    public string Script { get; set; } = string.Empty;
    public string ReadTags { get; set; } = string.Empty;
    public string WriteTags { get; set; } = string.Empty;
    public int HistoryWindowSec { get; set; }
    public bool AlarmSnapshot { get; set; }
    public CalcTrigger Trigger { get; set; } = CalcTrigger.Periodic;
    public int IntervalMs { get; set; } = 1000;
    public double ChangeDeadband { get; set; }
    public int ChangeCooldownMs { get; set; } = 1000;
    public AlarmLevel AlarmMinLevel { get; set; } = AlarmLevel.Info;
    public bool AlarmOnRaise { get; set; } = true;
    public bool AlarmOnAck { get; set; }
    public bool AlarmOnRecover { get; set; }
    public string Libraries { get; set; } = string.Empty;
    public string LibrarySource { get; set; } = string.Empty;
    public long Ts { get; set; }
    public string UserName { get; set; } = string.Empty;
}

// 可复用的 C# 局部函数。任务按名称引用，发布时把已发布源码并进任务副本。
public class ScriptLibrary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string PublishedSource { get; set; } = string.Empty;
    public long PublishedRevision { get; set; }
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string UserName { get; set; } = string.Empty;
}
