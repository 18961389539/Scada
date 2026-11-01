using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Engine.Alarms;

// 报警文案统一组装：规则里只写“现象/原因”这半句，触发时的具体数据（设备、当前值、单位、阈值、恢复条件、状态方向）
// 由这里补全，避免出现“出口温度高限”这种只有类型名、没有数值和对象的文案。
// 支持占位符：{device} {tag} {value} {unit} {threshold} {recover} {state} {previous} {bit} {raw} {quality} {expression}
// 用了占位符就不再追加括号；完全自定义整句时请用占位符，否则会在句末补上触发时的数据。
// 中文与英文走同一套逻辑：英文模板留空时自动生成英文，规则删除后事件仍保存两种文案快照。
public static class AlarmMessageBuilder
{
    public const int MaxLength = 500;

    private static readonly Regex Placeholder = new(
        @"\{(device|tag|value|unit|threshold|recover|state|previous|bit|raw|quality|expression)\}",
        RegexOptions.Compiled);

    public static string Build(Tag tag, Device? device, AlarmRule rule, double value, double? previousValue = null, TagQuality? quality = null) =>
        Compose(tag, device, rule, value, previousValue, quality ?? TagQuality.Good, english: false);

    public static string BuildEn(Tag tag, Device? device, AlarmRule rule, double value, double? previousValue = null, TagQuality? quality = null) =>
        Compose(tag, device, rule, value, previousValue, quality ?? TagQuality.Good, english: true);

    private static string Compose(Tag tag, Device? device, AlarmRule rule, double value, double? previousValue, TagQuality quality, bool english)
    {
        var unit = tag.Unit?.Trim() ?? string.Empty;
        var judged = rule.EvaluatedValue(value);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device"] = device?.Name ?? (english ? "Local" : "本机"),
            ["tag"] = tag.Name,
            ["value"] = Format(value, unit),
            ["unit"] = unit,
            ["threshold"] = Format(rule.Threshold, unit),
            ["recover"] = Recovery(rule) is double recover ? Format(recover, unit) : "",
            ["state"] = judged.ToString("0.###"),
            ["previous"] = previousValue is double previous ? previous.ToString("0.###") : "",
            ["bit"] = rule.BitIndex?.ToString() ?? "",
            ["raw"] = value.ToString("0.###"),
            ["quality"] = quality.ToString(),
            ["expression"] = rule.Expression ?? ""
        };

        // 空文案和“只写了类型名”的历史文案一律走自动生成，避免把“高限”两个字原样抛给操作员。
        var template = (english ? rule.MessageEn : rule.Message)?.Trim();
        var custom = !string.IsNullOrEmpty(template) &&
            !(english ? AlarmRuleText.IsPlaceholderOnlyEn(template) : AlarmRuleText.IsPlaceholderOnly(template));
        var text = custom
            ? Placeholder.Replace(template!, match => values.GetValueOrDefault(match.Groups[1].Value, ""))
            : Auto(tag, device, rule, values, value, previousValue, english);
        if (custom && !Placeholder.IsMatch(template!)) text += Suffix(rule, values, value, previousValue, english);
        return Trim(text, MaxLength);
    }

    // 阈值类报警的恢复门限；状态变化类没有阈值。
    private static double? Recovery(AlarmRule rule) => rule.Type switch
    {
        AlarmRuleType.High or AlarmRuleType.HighHigh => rule.Threshold - Math.Max(rule.RecoverDeadband, 0),
        AlarmRuleType.Low or AlarmRuleType.LowLow => rule.Threshold + Math.Max(rule.RecoverDeadband, 0),
        _ => null
    };

    private static string Auto(Tag tag, Device? device, AlarmRule rule, Dictionary<string, string> values, double value, double? previousValue, bool english)
    {
        var subject = device is null ? tag.Name : $"{device.Name} {tag.Name}";
        var label = english ? AlarmRuleText.LabelEn(rule.Type) : AlarmRuleText.Label(rule.Type);
        return rule.Type switch
        {
            AlarmRuleType.StateChange => english
                ? $"{subject} state change: {Direction(value, previousValue, true)}"
                : $"{subject} 状态变化：{Direction(value, previousValue, false)}",
            AlarmRuleType.Discrete => english
                ? $"{subject} {label}: {DiscreteState(rule, values, value, previousValue, true)}"
                : $"{subject} {label}：{DiscreteState(rule, values, value, previousValue, false)}",
            AlarmRuleType.BadQuality => english
                ? $"{subject} bad quality: current quality {values["quality"]}"
                : $"{subject} 质量坏（当前质量 {values["quality"]}）",
            AlarmRuleType.Expression => english
                ? $"{subject} composite condition met ({rule.Expression})"
                : $"{subject} 复合条件成立（{rule.Expression}）",
            _ => $"{subject} {label}{Suffix(rule, values, value, previousValue, english)}"
        };
    }

    // 位报警的当前状态：ON/OFF 给出电平，Change 给出翻转方向；数值字额外给出位号与原始字值。
    private static string DiscreteState(AlarmRule rule, Dictionary<string, string> values, double value, double? previousValue, bool english) =>
        rule.GetEffectiveActiveWhen() switch
        {
            AlarmActiveWhen.Change => Direction(rule.EvaluatedValue(value), previousValue is double p ? rule.EvaluatedValue(p) : null, english),
            AlarmActiveWhen.Off => english ? $"OFF ({StateText(rule, value, true)})" : $"OFF（{StateText(rule, value, false)}）",
            _ => english ? $"ON ({StateText(rule, value, true)})" : $"ON（{StateText(rule, value, false)}）"
        };

    private static string StateText(AlarmRule rule, double value, bool english) => rule.BitIndex is int bit
        ? english ? $"bit {bit} = {rule.EvaluatedValue(value):0.###}, word {value:0.###}" : $"bit {bit} = {rule.EvaluatedValue(value):0.###}，字值 {value:0.###}"
        : english ? $"current {rule.EvaluatedValue(value):0.###}" : $"当前 {rule.EvaluatedValue(value):0.###}";

    // 自定义文案的补充数据：阈值类给出当前值/阈值/恢复门限，状态类给出变化方向或当前位状态。
    private static string Suffix(AlarmRule rule, Dictionary<string, string> values, double value, double? previousValue, bool english)
    {
        if (rule.Type == AlarmRuleType.StateChange)
        {
            return english ? $" ({Direction(value, previousValue, true)})" : $"（{Direction(value, previousValue, false)}）";
        }

        if (rule.Type == AlarmRuleType.Discrete)
        {
            return rule.GetEffectiveActiveWhen() == AlarmActiveWhen.Change
                ? english ? $" ({Direction(rule.EvaluatedValue(value), previousValue, true)})" : $"（{Direction(rule.EvaluatedValue(value), previousValue, false)}）"
                : english ? $" ({StateText(rule, value, true)})" : $"（{StateText(rule, value, false)}）";
        }

        if (rule.Type == AlarmRuleType.BadQuality)
        {
            return english ? $" (current quality {values["quality"]})" : $"（当前质量 {values["quality"]}）";
        }

        if (rule.Type == AlarmRuleType.Expression)
        {
            return english ? $" (expression: {rule.Expression})" : $"（表达式：{rule.Expression}）";
        }

        var recover = values["recover"];
        var text = english ? $" (current {values["value"]}, threshold {values["threshold"]}" : $"（当前 {values["value"]}，阈值 {values["threshold"]}";
        if (recover.Length > 0 && rule.RecoverDeadband > 0)
        {
            text += english
                ? $", recover {(rule.Type is AlarmRuleType.Low or AlarmRuleType.LowLow ? ">=" : "<=")} {recover}"
                : $"，恢复 {(rule.Type is AlarmRuleType.Low or AlarmRuleType.LowLow ? "≥" : "≤")} {recover}";
        }

        return text + (english ? ")" : "）");
    }

    private static string Direction(double value, double? previousValue, bool english)
    {
        var current = value >= 0.5 ? 1 : 0;
        return previousValue is double previous ? $"{Bool(previous)} → {current}" : english ? $"current {current}" : $"当前 {current}";
    }

    private static int Bool(double value) => value >= 0.5 ? 1 : 0;

    private static string Format(double value, string unit) =>
        unit.Length == 0 ? value.ToString("0.###") : $"{value:0.###} {unit}";

    // 设备离线：必须带设备名与可读原因，历史实现只写了“设备离线：{异常}”，外发短信无法判断是哪台设备。
    public static string DeviceOffline(Device device, string? reason)
    {
        var detail = string.IsNullOrWhiteSpace(reason) ? "采集通信中断，未返回具体错误" : reason.Trim();
        return Trim($"设备离线：{device.Name}：{detail}", MaxLength);
    }

    public static string DeviceOfflineEn(Device device, string? reason)
    {
        var detail = string.IsNullOrWhiteSpace(reason) ? "Communication interrupted; no detail was reported" : reason.Trim();
        return Trim($"Device offline: {device.Name}: {detail}", MaxLength);
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];
}
