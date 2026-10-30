using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class AlarmRule
{
    public int Id { get; set; }
    // 稳定键：同一测点内唯一，用于工程包跨环境匹配，不随规则顺序或类型变化。空值在保存时自动生成。
    public string Key { get; set; } = "";
    public int TagId { get; set; }
    public AlarmRuleType Type { get; set; }
    public double Threshold { get; set; }
    public AlarmLevel Level { get; set; } = AlarmLevel.Warning;
    public int DelaySec { get; set; }
    public double RecoverDeadband { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Message { get; set; }
    // 原因：为什么会报（工艺/设备根源），外发通知与报警详情展示，避免操作员只看到现象。
    public string? Cause { get; set; }
    // 处置：现场该做什么（ISA-18.2 的 operator action），与原因一起构成可执行的报警说明。
    public string? Action { get; set; }
    // 恢复延时：恢复条件需持续该秒数才算恢复，与 DelaySec（触发延时）、RecoverDeadband（死区）共同抑制抖动。
    public int OffDelaySec { get; set; }
    // 按条件抑制（ISA-18.2 suppressed by design）：引用另一个测点，常用于“设备停机时不报”。
    public int? SuppressTagId { get; set; }
    public AlarmSuppressMode SuppressMode { get; set; }
    // 停用（out of service）：维修期间不评估，活动报警以“停用”结束；必须写明原因。
    public bool OutOfService { get; set; }
    public string? OutOfServiceReason { get; set; }
}
