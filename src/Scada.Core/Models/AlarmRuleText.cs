using Scada.Core.Enums;

namespace Scada.Core.Models;

// 报警文案的公共约定：类型标签，以及“只写了类型名”的判定。
// 引擎生成文案、接口校验、模板校验共用这一份，避免三处各写一套标签。
public static class AlarmRuleText
{
    public static string Label(AlarmRuleType type) => type switch
    {
        AlarmRuleType.HighHigh => "高高限",
        AlarmRuleType.High => "高限",
        AlarmRuleType.Low => "低限",
        AlarmRuleType.LowLow => "低低限",
        AlarmRuleType.StateChange => "状态变化",
        AlarmRuleType.Discrete => "位报警",
        AlarmRuleType.BadQuality => "质量坏",
        AlarmRuleType.Expression => "复合条件",
        _ => "报警"
    };

    // 英文类型标签：自动英文文案与英文界面共用，避免在引擎里散落英文字符串。
    public static string LabelEn(AlarmRuleType type) => type switch
    {
        AlarmRuleType.HighHigh => "High high",
        AlarmRuleType.High => "High",
        AlarmRuleType.Low => "Low",
        AlarmRuleType.LowLow => "Low low",
        AlarmRuleType.StateChange => "State change",
        AlarmRuleType.Discrete => "Discrete",
        AlarmRuleType.BadQuality => "Bad quality",
        AlarmRuleType.Expression => "Composite condition",
        _ => "Alarm"
    };

    // 英文文案只写类型名时同样没有信息量，按空文案走自动生成。
    public static bool IsPlaceholderOnlyEn(string? message)
    {
        var text = message?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        foreach (var type in Enum.GetValues<AlarmRuleType>())
        {
            if (string.Equals(text, LabelEn(type), StringComparison.OrdinalIgnoreCase)) return true;
        }

        return string.Equals(text, "Alarm", StringComparison.OrdinalIgnoreCase);
    }

    // 位报警触发条件归一化：旧 StateChange 在语义上等同 Change；非位报警类型忽略该字段。
    public static AlarmActiveWhen NormalizeActiveWhen(AlarmRuleType type, AlarmActiveWhen activeWhen) => type switch
    {
        AlarmRuleType.StateChange => AlarmActiveWhen.Change,
        AlarmRuleType.Discrete => activeWhen,
        _ => AlarmActiveWhen.On
    };

    // 只有类型名、没有对象和现象的文案等于没写（例如历史数据里的“高限”），按空文案处理。
    public static bool IsPlaceholderOnly(string? message)
    {
        var text = message?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        foreach (var type in Enum.GetValues<AlarmRuleType>())
        {
            if (string.Equals(text, Label(type), StringComparison.Ordinal)) return true;
        }

        return string.Equals(text, "报警", StringComparison.Ordinal);
    }
}
