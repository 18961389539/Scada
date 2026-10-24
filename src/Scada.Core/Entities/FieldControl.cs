namespace Scada.Core.Entities;

// 现场操作控制闭环（路线图第 7 项）。控制点配置按测点；挂牌、就地/远程按设备或测点；双人复核与顺控都有持久记录。

// 测点的控制策略。没有配置的可写测点沿用基础校验（量程、类型、回读）。
public class ControlPoint
{
    public int Id { get; set; }
    public int TagId { get; set; }
    // 服务端要求请求携带 confirmed=true（界面一律先弹确认，此项防止绕过界面直接调接口）。
    public bool RequireConfirm { get; set; } = true;
    // 双人复核：发起后进入待复核，由另一位对该测点有控制权限的用户批准后才执行。
    public bool DualApproval { get; set; }
    public int ApprovalTimeoutSec { get; set; } = 300;
    // 许可/互锁条件：受限数值表达式，结果非 0 才允许；引用的测点无有效值时视为不满足。
    public string? PermitExpression { get; set; }
    public string? PermitMessage { get; set; }
    // 限幅（比测点量程更严格的操作范围）与单次变化率限制（相对当前实时值的最大变化量）。
    public double? LimitMin { get; set; }
    public double? LimitMax { get; set; }
    public double? MaxStep { get; set; }
    // 独立反馈：写入并同点回读成功后，在超时内等待该表达式为真（例如阀位反馈到位），超时判失败。
    public string? FeedbackExpression { get; set; }
    public int FeedbackTimeoutMs { get; set; } = 10000;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}

public enum DeviceControlMode { Remote = 0, Local = 1 }

// 设备的就地/远程控制权：就地（现场接管）时拒绝一切中心下发。没有记录即远程。
public class DeviceControlState
{
    public int DeviceId { get; set; }
    public DeviceControlMode Mode { get; set; }
    public string? Reason { get; set; }
    public long ChangedTs { get; set; }
    public string? ChangedBy { get; set; }
}

public enum ControlLockoutKind { NoOperate = 0, Maintenance = 1 }

// 操作挂牌：禁止操作牌拒绝任何人；检修牌只允许挂牌人本人操作（检修调试）。摘牌保留记录。
public class ControlLockout
{
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    public int? TagId { get; set; }
    public ControlLockoutKind Kind { get; set; }
    public string Reason { get; set; } = "";
    public int? PlacedByUserId { get; set; }
    public string PlacedBy { get; set; } = "";
    public long PlacedTs { get; set; }
    public long? RemovedTs { get; set; }
    public string? RemovedBy { get; set; }
    public string? RemoveReason { get; set; }
}

public enum ControlRequestStatus { Pending = 0, Executed = 1, Failed = 2, Rejected = 3, Expired = 4, Cancelled = 5 }

// 待双人复核的控制请求（单点写入或启动顺控）。
public class ControlRequest
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "command"; // command | sequence
    public int? TagId { get; set; }
    public double? Value { get; set; }
    public int? SequenceId { get; set; }
    public string Source { get; set; } = "";
    public int RequestedByUserId { get; set; }
    public string RequestedBy { get; set; } = "";
    public long RequestedTs { get; set; }
    public long ExpiresTs { get; set; }
    public ControlRequestStatus Status { get; set; }
    public int? DecidedByUserId { get; set; }
    public string? DecidedBy { get; set; }
    public long? DecidedTs { get; set; }
    public string? Comment { get; set; }
    public string? ResultMessage { get; set; }
    public int? RunId { get; set; }
}

// 预置批量操作/顺控：步骤 JSON 为 ControlSequenceStep 数组。
public class ControlSequence
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int AreaId { get; set; } = Area.DefaultId;
    public string? Description { get; set; }
    public bool DualApproval { get; set; }
    public string StepsJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
    public long UpdatedTs { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class ControlSequenceStep
{
    public string Name { get; set; } = "";
    public int TagId { get; set; }
    public double Value { get; set; }
    // 本步完成条件：写入成功后在超时内等待其为真；为空则以写入及回读（及控制点反馈）成功为完成。
    public string? ConfirmExpression { get; set; }
    public int TimeoutMs { get; set; } = 10000;
    public int DelayMs { get; set; }
}

public enum ControlRunStatus { Running = 0, Completed = 1, Failed = 2, Aborted = 3 }

public class ControlSequenceRun
{
    public int Id { get; set; }
    public int SequenceId { get; set; }
    public string SequenceName { get; set; } = "";
    public int AreaId { get; set; }
    public ControlRunStatus Status { get; set; }
    public long StartedTs { get; set; }
    public long? EndedTs { get; set; }
    public int? StartedByUserId { get; set; }
    public string StartedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public string StepsJson { get; set; } = "[]"; // ControlStepResult 数组
    public string? Message { get; set; }
}

public sealed class ControlStepResult
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int TagId { get; set; }
    public double Value { get; set; }
    public string Status { get; set; } = "pending"; // pending | running | done | failed | skipped
    public string? Message { get; set; }
    public long? StartedTs { get; set; }
    public long? EndedTs { get; set; }
}
