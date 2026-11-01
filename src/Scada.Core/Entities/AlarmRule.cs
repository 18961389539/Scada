using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class AlarmRule
{
    public int Id { get; set; }
    // 稳定键：同一测点内唯一，用于工程包跨环境匹配，不随规则顺序或类型变化。空值在保存时自动生成。
    public string Key { get; set; } = "";
    public int TagId { get; set; }
    public AlarmRuleType Type { get; set; }
    // 位报警的触发条件（仅 Type=Discrete 使用）：On=1 报警、Off=0 报警、Change=每次翻转报警。
    // 旧数据的 StateChange 在语义上等同于 Discrete + Change。
    public AlarmActiveWhen ActiveWhen { get; set; }
    public double Threshold { get; set; }
    public AlarmLevel Level { get; set; } = AlarmLevel.Warning;
    // 治理属性：类别用于分组/筛选；后果与允许响应时间用于自动优先级，AutoPriority 为 true 时 Level 由服务端计算。
    public AlarmCategory Category { get; set; } = AlarmCategory.Process;
    public AlarmConsequence Consequence { get; set; } = AlarmConsequence.Major;
    public int ResponseTimeSec { get; set; } = 600;
    public bool AutoPriority { get; set; }
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

    // 有效触发条件：旧 StateChange 等价于位报警的变化触发；其他类型不关心 ActiveWhen。
    public AlarmActiveWhen GetEffectiveActiveWhen() => Type switch
    {
        AlarmRuleType.StateChange => AlarmActiveWhen.Change,
        AlarmRuleType.Discrete => ActiveWhen,
        _ => AlarmActiveWhen.On
    };

    // 自动优先级：后果 + 允许响应时间；接口、模板和工程包保存前都调用同一套计算。
    public void ApplyAutoPriority()
    {
        if (AutoPriority) Level = Scada.Core.Models.AlarmPriority.Compute(Consequence, ResponseTimeSec);
    }
}
