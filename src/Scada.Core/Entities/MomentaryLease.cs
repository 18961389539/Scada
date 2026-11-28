namespace Scada.Core.Entities;

public enum MomentaryLeaseKind { Press = 0, Pulse = 1 }

// Starting = 按下写入尚未确认（进程此时崩溃也要按释放处理，因为无法证明输出未动作）。
// Active = 已按下并在保持中，超过 ExpiresTs 必须释放。
public enum MomentaryLeaseState { Starting = 0, Active = 1 }

// 点动/瞬时按钮的持久化保持租约：进程崩溃或掉电后，启动时据此补写释放值。
// 只保存“尚未确认释放”的租约；释放成功后删除。
// Target* 记录按下时的现场目标；配置事后改地址或换设备时，释放必须写回这些字段而不是当前组态。
public class MomentaryLease
{
    public Guid Id { get; set; }
    public int TagId { get; set; }
    public double ReleaseValue { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Source { get; set; } = "";
    public MomentaryLeaseKind Kind { get; set; }
    public MomentaryLeaseState State { get; set; }
    public Guid ReleaseId { get; set; }
    public long CreatedTs { get; set; }
    public long StartDeadlineTs { get; set; }
    public long ExpiresTs { get; set; }
    public int? TargetDeviceId { get; set; }
    public int? TargetAddress { get; set; }
    public int? TargetDbNumber { get; set; }
    public int? TargetBitOffset { get; set; }
    public string? TargetNodeId { get; set; }
    public int? TargetDataType { get; set; }
    public double? TargetScale { get; set; }
    public double? TargetOffset { get; set; }
    public double? TargetMinValue { get; set; }
    public double? TargetMaxValue { get; set; }
}
