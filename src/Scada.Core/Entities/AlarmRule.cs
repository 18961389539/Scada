using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class AlarmRule
{
    public int Id { get; set; }
    public int TagId { get; set; }
    public AlarmRuleType Type { get; set; }
    public double Threshold { get; set; }
    public AlarmLevel Level { get; set; } = AlarmLevel.Warning;
    public int DelaySec { get; set; }
    public double RecoverDeadband { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Message { get; set; }
    // 恢复延时：恢复条件需持续该秒数才算恢复，与 DelaySec（触发延时）、RecoverDeadband（死区）共同抑制抖动。
    public int OffDelaySec { get; set; }
    // 按条件抑制（ISA-18.2 suppressed by design）：引用另一个测点，常用于“设备停机时不报”。
    public int? SuppressTagId { get; set; }
    public AlarmSuppressMode SuppressMode { get; set; }
    // 停用（out of service）：维修期间不评估，活动报警以“停用”结束；必须写明原因。
    public bool OutOfService { get; set; }
    public string? OutOfServiceReason { get; set; }
}
