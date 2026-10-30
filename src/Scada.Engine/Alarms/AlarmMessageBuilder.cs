using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Engine.Alarms;

// 报警文案统一组装：规则里只写“现象/原因”这半句，触发时的具体数据（设备、当前值、单位、阈值、恢复条件、状态方向）
// 由这里补全，避免出现“出口温度高限”这种只有类型名、没有数值和对象的文案。
// 支持占位符：{device} {tag} {value} {unit} {threshold} {recover} {state} {previous}
// 用了占位符就不再追加括号；完全自定义整句时请用占位符，否则会在句末补上触发时的数据。
public static class AlarmMessageBuilder
{
    public const int MaxLength = 500;

    private static readonly Regex Placeholder = new(@"\{(device|tag|value|unit|threshold|recover|state|previous)\}", RegexOptions.Compiled);

    public static string Build(Tag tag, Device? device, AlarmRule rule, double value, double? previousValue = null)
    {
        var unit = tag.Unit?.Trim() ?? string.Empty;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device"] = device?.Name ?? "本机",
            ["tag"] = tag.Name,
            ["value"] = Format(value, unit),
            ["unit"] = unit,
            ["threshold"] = Format(rule.Threshold, unit),
            ["recover"] = Recovery(rule) is double recover ? Format(recover, unit) : "",
            ["state"] = value.ToString("0.###"),
            ["previous"] = previousValue?.ToString("0.###") ?? ""
        };

        // 空文案和“只写了类型名”的历史文案一律走自动生成，避免把“高限”两个字原样抛给操作员。
        var template = rule.Message?.Trim();
        var custom = !string.IsNullOrEmpty(template) && !AlarmRuleText.IsPlaceholderOnly(template);
        var text = custom
            ? Placeholder.Replace(template!, match => values[match.Groups[1].Value])
            : Auto(tag, device, rule, values, value, previousValue);
        if (custom && !Placeholder.IsMatch(template!)) text += Suffix(rule, values, value, previousValue);
        return Trim(text, MaxLength);
    }

    // 阈值类报警的恢复门限；状态变化类没有阈值。
    private static double? Recovery(AlarmRule rule) => rule.Type switch
    {
        AlarmRuleType.High or AlarmRuleType.HighHigh => rule.Threshold - Math.Max(rule.RecoverDeadband, 0),
        AlarmRuleType.Low or AlarmRuleType.LowLow => rule.Threshold + Math.Max(rule.RecoverDeadband, 0),
        _ => null
    };

    private static string Auto(Tag tag, Device? device, AlarmRule rule, Dictionary<string, string> values, double value, double? previousValue)
    {
        var subject = device is null ? tag.Name : $"{device.Name} {tag.Name}";
        return rule.Type == AlarmRuleType.StateChange
            ? $"{subject} 状态变化：{Direction(value, previousValue)}"
            : $"{subject} {AlarmRuleText.Label(rule.Type)}{Suffix(rule, values, value, previousValue)}";
    }

    // 自定义文案的补充数据：阈值类给出当前值/阈值/恢复门限，状态类给出变化方向。
    private static string Suffix(AlarmRule rule, Dictionary<string, string> values, double value, double? previousValue)
    {
        if (rule.Type == AlarmRuleType.StateChange) return $"（{Direction(value, previousValue)}）";
        var recover = values["recover"];
        var text = $"（当前 {values["value"]}，阈值 {values["threshold"]}";
        if (recover.Length > 0 && rule.RecoverDeadband > 0)
        {
            text += $"，恢复 {(rule.Type is AlarmRuleType.Low or AlarmRuleType.LowLow ? "≥" : "≤")} {recover}";
        }

        return text + "）";
    }

    private static string Direction(double value, double? previousValue)
    {
        var current = value >= 0.5 ? 1 : 0;
        return previousValue is double previous ? $"{Bool(previous)} → {current}" : $"当前 {current}";
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

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];
}
