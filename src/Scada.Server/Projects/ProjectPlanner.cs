using System.Text.Json.Nodes;

namespace Scada.Server.Projects;

public enum ConflictResolution { Overwrite, Skip, Rename }

public sealed record FieldChange(string Field, string? Old, string? New);

public sealed class PlanEntry
{
    public string Section { get; init; } = "";
    public string Key { get; init; } = "";          // 包内原始键
    public string FinalKey { get; set; } = "";      // 改名/引用改名后的键
    // add | update | rename | skip | delete | unchanged
    public string Action { get; set; } = "unchanged";
    public string[] Areas { get; set; } = [];
    public List<FieldChange> Changes { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public JsonObject? Data { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public JsonObject? Current { get; set; }
}

public sealed class ProjectPlan
{
    public List<PlanEntry> Entries { get; } = [];
    public List<string> Warnings { get; } = [];
    public IEnumerable<PlanEntry> Effective => Entries.Where(x => x.Action is "add" or "update" or "rename" or "delete");
}

// 对比当前工程与目标包，得出逐项动作。
// 导入：新增/覆盖/跳过/改名（冲突=同键但内容不同），不删除目标中多出的对象。
// 镜像（回滚、版本比较）：目标即全部，当前多出的对象删除，冲突一律覆盖。
public static class ProjectPlanner
{
    public static ProjectPlan Build(ProjectPackage current, ProjectPackage incoming, bool mirror, ConflictResolution defaultResolution, IReadOnlyDictionary<string, ConflictResolution>? overrides = null)
    {
        var plan = new ProjectPlan();
        var renames = ProjectFormat.Sections.ToDictionary(x => x, _ => new Dictionary<string, string>());
        foreach (var section in ProjectFormat.Sections)
        {
            var existing = current.Section(section).ToDictionary(x => x.Key);
            var items = incoming.Section(section).Select(x => (Item: x, Data: (JsonObject)x.Data.DeepClone())).ToList();
            var usedKeys = new HashSet<string>(existing.Keys.Concat(items.Select(x => x.Item.Key)));
            var entries = new List<(PlanEntry Entry, ProjectItem Item)>();
            // 1. 先套用前面分区的改名（引用改名会改变本分区的键，例如设备改名后其测点键随之变化）。
            foreach (var (item, data) in items)
            {
                RemapRefs(data, renames, section, crossOnly: true);
                var key = ProjectFormat.KeyOf(section, data, item.Key);
                if (key != item.Key) renames[section][item.Key] = key;
                entries.Add((new PlanEntry { Section = section, Key = item.Key, FinalKey = key, Data = data, Areas = item.Areas }, item));
            }
            // 2. 冲突且选择“改名”的，换一个不冲突的名称。
            foreach (var (entry, _) in entries)
            {
                if (!existing.TryGetValue(entry.FinalKey, out var target) || System.Text.Json.Nodes.JsonNode.DeepEquals(target.Data, entry.Data)) continue;
                var resolution = mirror ? ConflictResolution.Overwrite : overrides?.GetValueOrDefault($"{section}:{entry.Key}", defaultResolution) ?? defaultResolution;
                if (resolution != ConflictResolution.Rename) continue;
                if (section == "controlPoints") continue; // 每个测点只有一份控制策略，无法改名；按覆盖处理
                if (section == "alarmRules")
                {
                    // 报警规则无名称：“改名”即换一个新的稳定键，作为同测点的另一条规则新增。
                    var data = (JsonObject)entry.Data!.DeepClone(); data["key"] = Guid.NewGuid().ToString("N");
                    var newKey = ProjectFormat.KeyOf(section, data); usedKeys.Add(newKey);
                    entry.Data = data; entry.FinalKey = newKey; entry.Action = "rename";
                    continue;
                }
                var name = ProjectFormat.Name(entry.Data!);
                for (var n = 1; ; n++)
                {
                    var candidate = n == 1 ? $"{name}_导入" : $"{name}_导入{n}";
                    var data = (JsonObject)entry.Data!.DeepClone(); data["name"] = candidate;
                    var key = ProjectFormat.KeyOf(section, data, entry.FinalKey);
                    if (usedKeys.Contains(key)) continue;
                    usedKeys.Add(key); entry.Data = data; renames[section][entry.Key] = key; entry.FinalKey = key; entry.Action = "rename";
                    break;
                }
            }
            // 3. 本分区内部引用（计算测点表达式引用测点、画面跳转引用画面）。
            foreach (var (entry, _) in entries) RemapRefs(entry.Data!, renames, section, crossOnly: false);
            // 4. 决定最终动作。
            foreach (var (entry, item) in entries)
            {
                if (entry.Action == "rename") { entry.Changes = Diff(null, entry.Data); plan.Entries.Add(entry); continue; }
                if (!existing.TryGetValue(entry.FinalKey, out var target)) { entry.Action = "add"; entry.Changes = Diff(null, entry.Data); }
                else
                {
                    entry.Current = target.Data; entry.Areas = target.Areas.Union(entry.Areas).ToArray();
                    if (System.Text.Json.Nodes.JsonNode.DeepEquals(target.Data, entry.Data)) entry.Action = "unchanged";
                    else
                    {
                        var resolution = mirror ? ConflictResolution.Overwrite : overrides?.GetValueOrDefault($"{section}:{entry.Key}", defaultResolution) ?? defaultResolution;
                        entry.Action = resolution == ConflictResolution.Skip ? "skip" : "update";
                        entry.Changes = Diff(target.Data, entry.Data);
                    }
                }
                plan.Entries.Add(entry);
            }
            if (mirror)
            {
                var finalKeys = entries.Select(x => x.Entry.FinalKey).ToHashSet();
                foreach (var item in current.Section(section).Where(x => !finalKeys.Contains(x.Key)))
                    plan.Entries.Add(new PlanEntry { Section = section, Key = item.Key, FinalKey = item.Key, Action = "delete", Areas = item.Areas, Current = item.Data, Changes = Diff(item.Data, null) });
            }
        }
        foreach (var entry in plan.Entries.Where(x => x.Data is not null && x.Action is "add" or "update" or "rename"))
            foreach (var missing in MissingEnvironmentRefs(entry.Data!)) plan.Warnings.Add($"{ProjectFormat.SectionLabels[entry.Section]} {entry.FinalKey}：{missing}");
        return plan;
    }

    private static IEnumerable<string> MissingEnvironmentRefs(JsonObject data)
    {
        foreach (var (field, value) in data)
            if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith("#缺失:")) yield return $"{field} 引用的对象在导出环境中已不存在";
    }

    // 把引用字段与表达式中的键按改名表替换。crossOnly=true 时只替换其他分区的引用。
    public static void RemapRefs(JsonNode? node, IReadOnlyDictionary<string, Dictionary<string, string>> renames, string section, bool crossOnly)
    {
        bool Use(string target) => crossOnly ? target != section : target == section;
        string Map(string target, string key) => Use(target) && renames[target].TryGetValue(key, out var renamed) ? renamed : key;
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(x => x.Key).ToList())
                {
                    var value = obj[name];
                    if (ProjectFormat.RefTargets.TryGetValue(name, out var target) && target is not null)
                    {
                        if (value is JsonValue single && single.TryGetValue<string>(out var key)) obj[name] = Map(target, key);
                        else if (value is JsonArray array) obj[name] = new JsonArray(array.Select(x => (JsonNode?)JsonValue.Create(x is JsonValue jv && jv.TryGetValue<string>(out var k) ? Map(target, k) : x?.ToString())).ToArray());
                    }
                    else if (ProjectFormat.ExpressionFields.Contains(name) && value is JsonValue text && text.TryGetValue<string>(out var expression))
                        obj[name] = ProjectFormat.KeyReference.Replace(expression, m => "#{" + Map("tags", m.Groups[1].Value) + "}");
                    else RemapRefs(value, renames, section, crossOnly);
                }
                break;
            case JsonArray array:
                foreach (var child in array) RemapRefs(child, renames, section, crossOnly);
                break;
        }
    }

    public static List<FieldChange> Diff(JsonObject? before, JsonObject? after)
    {
        var fields = (before?.Select(x => x.Key) ?? []).Union(after?.Select(x => x.Key) ?? []).Order(StringComparer.Ordinal);
        var changes = new List<FieldChange>();
        foreach (var field in fields)
        {
            var a = before?[field]; var b = after?[field];
            if (JsonNode.DeepEquals(a, b)) continue;
            changes.Add(new FieldChange(field, Text(a), Text(b)));
        }
        return changes;
    }

    private static string? Text(JsonNode? node)
    {
        if (node is null) return null;
        var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
