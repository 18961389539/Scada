using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Models;

public sealed class ObjectTemplateDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<ObjectTagDefinition> Tags { get; set; } = [];
    public List<ObjectAlarmDefinition> Alarms { get; set; } = [];
    public ScreenDocument Panel { get; set; } = new() { Width = 800, Height = 480 };
    public Dictionary<string, string> Bindings { get; set; } = [];
    public Dictionary<string, string> ActionBindings { get; set; } = [];
}

public sealed class ObjectTagDefinition
{
    public string Key { get; set; } = string.Empty;
    public int AddressOffset { get; set; }
    public Tag Properties { get; set; } = new() { DeviceId = 1 };
}

public sealed class ObjectAlarmDefinition
{
    public string Key { get; set; } = string.Empty;
    public string TagKey { get; set; } = string.Empty;
    public AlarmRule Properties { get; set; } = new() { TagId = 1 };
}

public sealed class ObjectInstanceBaseline
{
    public Dictionary<string, Tag> Tags { get; set; } = [];
    public Dictionary<string, AlarmRule> Alarms { get; set; } = [];
    public string ScreenName { get; set; } = string.Empty;
    public ScreenDocument Panel { get; set; } = new();
}

public sealed record ObjectInstanceParameters(string Name, int DeviceId, int BaseAddress);
public sealed record TemplateConflict(string Key, string Label, string Baseline, string Local, string Incoming, string[] Choices);
public sealed record TemplateChange(string Key, string Action, string Detail);

public static class ObjectTemplateJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
    public static T Copy<T>(T value) => Read<T>(Write(value));
    public static bool Equal<T>(T left, T right) => Write(left) == Write(right);
}

public static partial class ObjectTemplateValidator
{
    public static IReadOnlyList<string> Validate(ObjectTemplateDocument? definition, bool forPublish = true)
    {
        var errors = new List<string>();
        if (definition is null) return ["模板内容不能为空"];
        if (definition.SchemaVersion != 1) errors.Add("模板格式版本不支持");
        if (definition.Tags is null || definition.Tags.Count > 500 || forPublish && definition.Tags.Count == 0)
            return ["模板须包含 1～500 个测点，草稿允许暂不配置测点"];
        if (definition.Alarms is null || definition.Alarms.Count > 2000 || definition.Panel is null || definition.Bindings is null || definition.ActionBindings is null)
            return ["模板报警、面板或绑定配置无效"];
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new Dictionary<string, Tag>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in definition.Tags)
        {
            if (item is null || !ValidKey(item.Key) || !keys.Add(item.Key)) { errors.Add("测点逻辑键无效或重复"); continue; }
            if (item.Properties is null) { errors.Add($"{item.Key} 测点定义不能为空"); continue; }
            var tag = ObjectTemplateJson.Copy(item.Properties);
            tag.Id = tags.Count + 1; tag.DeviceId = 1; tag.Address = item.AddressOffset;
            if (string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 50 || !names.Add(tag.Name)) errors.Add($"{item.Key} 名称须为不重复的 1～50 个字符");
            var error = ConfigurationValidator.Tag(tag);
            if (error is not null) errors.Add($"{item.Key}：{error}");
            tags[item.Key] = tag;
        }
        keys.Clear();
        foreach (var item in definition.Alarms)
        {
            if (item is null || !ValidKey(item.Key) || !keys.Add(item.Key)) { errors.Add("报警逻辑键无效或重复"); continue; }
            if (item.Properties is null) { errors.Add($"{item.Key} 报警定义不能为空"); continue; }
            if (forPublish && (item.TagKey is null || !tags.ContainsKey(item.TagKey))) errors.Add($"{item.Key} 引用了不存在的测点逻辑键");
            var alarm = ObjectTemplateJson.Copy(item.Properties); alarm.TagId = 1;
            var error = ConfigurationValidator.Alarm(alarm);
            if (error is not null) errors.Add($"{item.Key}：{error}");
        }
        // Templates keep symbolic keys. Concrete tag IDs are only assigned when creating an instance.
        var panel = ObjectTemplateJson.Copy(definition.Panel);
        if (panel.Nodes is null) return [.. errors, "面板图元不能为空"];
        foreach (var node in panel.Nodes)
        {
            if (node is null || node.Action is null) continue;
            if (node.TagId is not null || node.Action.TagId is not null) errors.Add("模板面板必须通过测点逻辑键绑定，不能保存现场测点编号");
            if (definition.Bindings.TryGetValue(node.Id ?? "", out var key))
            {
                if (key is not null && tags.TryGetValue(key, out var tag)) node.TagId = tag.Id;
                else if (forPublish) errors.Add($"图元 {node.Id} 的输入逻辑键不存在");
            }
            if (node.Action.Type == "command")
            {
                if (definition.ActionBindings.TryGetValue(node.Id ?? "", out var actionKey) && actionKey is not null && tags.TryGetValue(actionKey, out var tag)) node.Action.TagId = tag.Id;
                else if (!forPublish) node.Action.Type = "none";
            }
        }
        errors.AddRange(ScreenValidator.Validate(panel, forPublish ? tags.Values.ToDictionary(x => x.Id) : null).Select(x => $"面板 {x.NodeId}：{x.Message}"));
        if (forPublish)
        {
            var nodeIds = panel.Nodes.Where(n => n is not null).Select(n => n.Id).ToHashSet();
            foreach (var id in definition.Bindings.Keys.Concat(definition.ActionBindings.Keys))
                if (!nodeIds.Contains(id)) errors.Add($"面板绑定 {id} 对应的图元不存在");
        }
        return errors;
    }
    public static bool ValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex KeyPattern();
}

public static class ConfigurationValidator
{
    public static string? Tag(Tag tag)
    {
        if (tag.DeviceId <= 0) return "必须指定设备";
        if (string.IsNullOrWhiteSpace(tag.Name) || tag.Name.Length > 100) return "测点名称须为 1～100 个字符";
        if (!Enum.IsDefined(tag.Area) || !Enum.IsDefined(tag.DataType) || !Enum.IsDefined(tag.ArchiveMode)) return "测点类型无效";
        if (tag.Address < 0 || (long)tag.Address + tag.RegisterCount - 1 > 65535) return "地址超出 Modbus 范围";
        if (tag.Area is TagArea.Coil or TagArea.DiscreteInput && tag.DataType != TagDataType.Bool) return "线圈/离散输入区域只能使用 Bool 类型";
        if (tag.Writable && tag.Area is TagArea.DiscreteInput or TagArea.InputRegister) return "只读区域不能设置为可写";
        if (!double.IsFinite(tag.Deadband) || tag.Deadband < 0) return "死区不能为负数或非有限值";
        if (!double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset) || tag.Writable && tag.Scale == 0) return "倍率或偏移无效，可写测点的倍率不能为零";
        if (tag.ArchiveMode == ArchiveMode.Periodic && tag.ArchivePeriodSec < 1) return "周期存储的周期必须大于 0";
        if (tag.MinValue is double min && !double.IsFinite(min) || tag.MaxValue is double max && !double.IsFinite(max)) return "量程必须是有限值";
        if (tag.MinValue > tag.MaxValue) return "最小值不能大于最大值";
        if (tag.Description?.Length > 500 || tag.Unit?.Length > 32) return "描述或单位过长";
        return null;
    }

    public static string? Alarm(AlarmRule alarm)
    {
        if (alarm.TagId <= 0) return "必须指定测点";
        if (!Enum.IsDefined(alarm.Type) || !Enum.IsDefined(alarm.Level)) return "报警类型或级别无效";
        if (!double.IsFinite(alarm.Threshold)) return "报警阈值无效";
        if (alarm.DelaySec < 0 || !double.IsFinite(alarm.RecoverDeadband) || alarm.RecoverDeadband < 0) return "报警延时或恢复死区无效";
        if (alarm.Message?.Length > 500) return "报警消息不能超过 500 个字符";
        return null;
    }
}
