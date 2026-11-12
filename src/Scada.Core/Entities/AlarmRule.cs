using System.Text.Json.Serialization;
using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class AlarmRule : IAuditedEntity
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
    // 英文文案可选：留空时引擎自动生成英文；填写后可用同样的 {device}/{tag}/{value} 等占位符。
    public string? MessageEn { get; set; }
    public string? CauseEn { get; set; }
    public string? ActionEn { get; set; }
    // 原因：为什么会报（工艺/设备根源），外发通知与报警详情展示，避免操作员只看到现象。
    public string? Cause { get; set; }
    // 处置：现场该做什么（ISA-18.2 的 operator action），与原因一起构成可执行的报警说明。
    public string? Action { get; set; }
    // 报警字逐位映射：位报警用于数值字（Int16/UInt16/Int32/UInt32）时指定取第几位（0 起）。
    public int? BitIndex { get; set; }
    // 复合条件表达式（#测点号引用多个测点）；仅 Type=Expression 时参与判定，结果为非 0 即条件成立。
    public string? Expression { get; set; }
    // 恢复延时：恢复条件需持续该秒数才算恢复，与 DelaySec（触发延时）、RecoverDeadband（死区）共同抑制抖动。
    public int OffDelaySec { get; set; }
    // 按条件抑制（ISA-18.2 suppressed by design）：引用另一个测点，常用于“设备停机时不报”。
    public int? SuppressTagId { get; set; }
    public AlarmSuppressMode SuppressMode { get; set; }
    // 停用（out of service）：维修期间不评估，活动报警以“停用”结束；必须写明原因。
    public bool OutOfService { get; set; }
    public string? OutOfServiceReason { get; set; }

    // 判定值：位映射报警取指定二进制位，其余类型直接使用原始值；评估、消息、模拟共用。
    public double EvaluatedValue(double raw) =>
        BitIndex is int bit && bit is >= 0 and < 64
            ? (((long)Math.Truncate(raw) & (1L << bit)) != 0 ? 1 : 0)
            : raw;

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

    // REST 与 MCP 的唯一可编辑字段清单：两边只负责填入要改的值，复制不再各写一份（漏字段会让校验看到残缺对象）。
    public void ApplyEditableFrom(AlarmRule other)
    {
        TagId = other.TagId; Type = other.Type; ActiveWhen = other.ActiveWhen; Threshold = other.Threshold;
        Level = other.Level; Category = other.Category; Consequence = other.Consequence; ResponseTimeSec = other.ResponseTimeSec;
        AutoPriority = other.AutoPriority; DelaySec = other.DelaySec; RecoverDeadband = other.RecoverDeadband; Enabled = other.Enabled;
        Message = other.Message; MessageEn = other.MessageEn; Cause = other.Cause; CauseEn = other.CauseEn;
        Action = other.Action; ActionEn = other.ActionEn; OffDelaySec = other.OffDelaySec; BitIndex = other.BitIndex;
        Expression = other.Expression; SuppressTagId = other.SuppressTagId; SuppressMode = other.SuppressMode;
    }

    // 报警规则没有独立名称，审计文案沿用「测点 #id」的既有写法。
    // 三个成员是给审计拦截器用的派生属性，不能进工程包/接口契约：
    // 导出直接序列化实体，TagId 在导入时会重映射，auditName 会因此被误判成「字段有变化」。
    [JsonIgnore] public string AuditTargetType => "AlarmRule";
    [JsonIgnore] public string AuditLabel => "报警规则";
    [JsonIgnore] public string AuditName => $"测点 #{TagId}";
}
