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
        _ => "报警"
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
