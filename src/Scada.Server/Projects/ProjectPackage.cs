using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;

namespace Scada.Server.Projects;

public sealed class ProjectException(string message, int status = 400) : Exception(message) { public int Status { get; } = status; }

// 工程包：按“自然键”（名称）而不是数据库编号互相引用，便于跨环境迁移与版本比较。
// 引用字段统一改名为 xxxRef（如 areaRef、tagRef），表达式/脚本中的测点写作 #{键}。
public sealed class ProjectItem
{
    public string Key { get; set; } = "";
    // 所属区域名称（区域内对象）；全局对象为空。用于授权与可见性过滤。
    public string[] Areas { get; set; } = [];
    public JsonObject Data { get; set; } = new();
}

public sealed class ProjectPackage
{
    public int Format { get; set; } = 1;
    public string Product { get; set; } = "Scada";
    public long ExportedTs { get; set; }
    public string ExportedBy { get; set; } = "";
    // 按导出人权限过滤过（不是完整工程）；此类包不能用于回滚。
    public bool Partial { get; set; }
    public Dictionary<string, List<ProjectItem>> Sections { get; set; } = new();
    public List<ProjectItem> Section(string name) => Sections.TryGetValue(name, out var list) ? list : Sections[name] = [];
}

public static class ProjectFormat
{
    public static readonly string[] Sections = ["areas", "roles", "devices", "tags", "calcJobs", "alarmRules", "reportTemplates", "objectTemplates", "screens", "controlPoints", "controlSequences", "recipeTemplates"];
    public static readonly Dictionary<string, string> SectionLabels = new()
    {
        ["areas"] = "区域", ["roles"] = "角色", ["devices"] = "设备", ["tags"] = "测点", ["calcJobs"] = "计算任务", ["alarmRules"] = "报警规则",
        ["reportTemplates"] = "报表模板", ["objectTemplates"] = "对象模板", ["screens"] = "画面", ["controlPoints"] = "控制策略", ["controlSequences"] = "顺控", ["recipeTemplates"] = "配方"
    };
    public static readonly HashSet<string> GlobalSections = ["areas", "roles", "objectTemplates", "screens"];
    // 引用字段 → 被引用的分区；null 表示环境对象（网关、通知通道），不随工程迁移，只按名称匹配。
    public static readonly Dictionary<string, string?> RefTargets = new()
    {
        ["areaRef"] = "areas", ["deviceRef"] = "devices", ["gatewayRef"] = null, ["emailChannelRef"] = null, ["jobRef"] = "calcJobs",
        ["tagRef"] = "tags", ["inputTagRef"] = "tags", ["suppressTagRef"] = "tags", ["readTagRefs"] = "tags", ["writeTagRefs"] = "tags", ["screenRef"] = "screens"
    };
    public static readonly HashSet<string> ExpressionFields = ["expression", "script", "permitExpression", "feedbackExpression", "confirmExpression"];
    public static readonly Regex KeyReference = new(@"#\{([^}]*)\}", RegexOptions.Compiled);
    public static readonly Regex IdReference = new(@"#(\d+)", RegexOptions.Compiled);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Pretty = new(Json) { WriteIndented = true };

    public static string KeyOf(string section, JsonObject data, string? previousKey = null) => section switch
    {
        "controlPoints" => data["tagRef"]?.GetValue<string>() ?? "",
        "tags" => data["deviceRef"]?.GetValue<string>() is string device ? $"{device}/{Name(data)}" : $"@{Name(data)}",
        "alarmRules" => data["key"]?.GetValue<string>() is { Length: > 0 } ruleKey ? $"{data["tagRef"]?.GetValue<string>()}|{ruleKey}"
            : LegacyRuleKey(data, previousKey),
        _ => Name(data)
    };
    public static string Name(JsonObject data) => data["name"]?.GetValue<string>() ?? "";
    // 旧格式（无稳定键）报警规则的键：测点|类型|同测点同类型内按编号排序的序号。
    public static string LegacyRuleKey(JsonObject data, string? previousKey) => $"{data["tagRef"]?.GetValue<string>()}|{data["type"]?.GetValue<string>()}|{Ordinal(previousKey)}";

    // 兼容旧工程包/旧快照：给无稳定键的报警规则补键。优先按旧键（测点|类型|序号）对应到当前库中的规则沿用其稳定键，
    // 否则用确定性的 legacy-类型-序号，保证同一个旧包重复导入得到同一个键。
    public static void UpgradeLegacyRules(ProjectPackage package, IReadOnlyDictionary<string, string> currentLegacyKeys)
    {
        var rules = package.Section("alarmRules");
        for (var i = 0; i < rules.Count; i++)
        {
            var item = rules[i];
            if (item.Data["key"]?.GetValue<string>() is { Length: > 0 }) continue;
            var legacy = LegacyRuleKey(item.Data, item.Key);
            item.Data["key"] = currentLegacyKeys.GetValueOrDefault(legacy) ?? $"legacy-{item.Data["type"]?.GetValue<string>()?.ToLowerInvariant()}-{Ordinal(item.Key)}";
            item.Key = KeyOf("alarmRules", item.Data);
        }
        var dup = rules.GroupBy(x => x.Key).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new ProjectException($"报警规则 键重复：{dup.Key}");
    }

    public static int Ordinal(string? key) => key is not null && int.TryParse(key[(key.LastIndexOf('|') + 1)..], out var n) ? n : 1;

    // 内容哈希：只覆盖各分区内容（键排序），与导出时间、导出人无关。
    public static string ContentHash(ProjectPackage package)
    {
        var root = new JsonObject();
        foreach (var section in Sections)
            root[section] = new JsonArray(package.Section(section).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JsonNode)new JsonObject { ["key"] = x.Key, ["data"] = x.Data.DeepClone() }).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    public static Dictionary<string, int> Summary(ProjectPackage package) => Sections.ToDictionary(x => x, x => package.Section(x).Count);

    public static ProjectPackage Parse(string json)
    {
        ProjectPackage? package;
        try { package = JsonSerializer.Deserialize<ProjectPackage>(json, Json); }
        catch (JsonException ex) { throw new ProjectException($"工程包格式无效：{ex.Message}"); }
        if (package is null || package.Format != 1) throw new ProjectException("工程包版本不受支持（需要 format=1）");
        foreach (var name in package.Sections.Keys) if (!Sections.Contains(name)) throw new ProjectException($"未知分区：{name}");
        foreach (var section in Sections)
        {
            var items = package.Section(section);
            foreach (var item in items) if (item.Data is null) throw new ProjectException($"{SectionLabels[section]} {item.Key} 缺少内容");
            var dup = items.GroupBy(x => x.Key).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null) throw new ProjectException($"{SectionLabels[section]} 键重复：{dup.Key}");
        }
        return package;
    }
}

// 从数据库导出工程包。include(section, 区域编号集合) 决定是否包含某项（按权限过滤）。
public static class ProjectExporter
{
    private static readonly string[] FixedOutputs = ["hours", "periodHours", "sinceMaintenance", "starts", "periodStarts", "maintenanceDue"];

    // 当前库中报警规则的旧格式键（测点|类型|序号）→ 稳定键，用于兼容旧工程包与旧快照。
    public static async Task<Dictionary<string, string>> LegacyRuleKeysAsync(ConfigDbContext db, CancellationToken ct)
    {
        var deviceNames = await db.Devices.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var tagKeys = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DeviceId is int d ? $"{deviceNames.GetValueOrDefault(d)}/{x.Name}" : $"@{x.Name}", ct);
        var result = new Dictionary<string, string>();
        foreach (var group in (await db.AlarmRules.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct)).GroupBy(x => (x.TagId, x.Type)))
        {
            var n = 0;
            foreach (var rule in group) result[$"{tagKeys.GetValueOrDefault(rule.TagId)}|{rule.Type}|{++n}"] = rule.Key;
        }
        return result;
    }

    public static async Task<ProjectPackage> ExportAsync(ConfigDbContext db, AccessModel model, Func<string, int[], bool>? include, string exportedBy, CancellationToken ct)
    {
        var areas = await db.Areas.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var roles = await db.AccessRoles.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var devices = await db.Devices.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var tags = await db.Tags.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var jobs = await db.CalcJobs.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var rules = await db.AlarmRules.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var reports = await db.ReportTemplates.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var controlPoints = await db.ControlPoints.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var sequences = await db.ControlSequences.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var recipeTemplates = await db.RecipeTemplates.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var recipes = await db.Recipes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var recipeVersions = (await db.RecipeVersions.AsNoTracking().ToListAsync(ct)).ToDictionary(x => (x.RecipeId, x.Version));
        var templates = await db.ObjectTemplates.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var versions = await db.ObjectTemplateVersions.AsNoTracking().OrderBy(x => x.Version).ToListAsync(ct);
        var screens = await db.ProcessScreens.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var gateways = await db.Gateways.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var channels = await db.NotificationChannels.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var areaNames = areas.ToDictionary(x => x.Id, x => x.Name);
        var deviceNames = devices.ToDictionary(x => x.Id, x => x.Name);
        var deviceArea = devices.ToDictionary(x => x.Id, x => x.AreaId);
        var jobNames = jobs.ToDictionary(x => x.Id, x => x.Name);
        var screenNames = screens.ToDictionary(x => x.Id, x => x.Name);
        var tagKeys = tags.ToDictionary(x => x.Id, x => x.DeviceId is int d ? $"{deviceNames.GetValueOrDefault(d)}/{x.Name}" : $"@{x.Name}");
        foreach (var key in tagKeys.Values.Concat(deviceNames.Values)) if (key.Contains('}')) throw new ProjectException($"名称不能包含 }}：{key}");
        int[] TagAreaIds(Tag t) => t.DeviceId is int d ? [deviceArea.GetValueOrDefault(d, Area.DefaultId)] : t.AreaId is int a ? [a] : model.AreasOfTag(t.Id);
        string[] Names(IEnumerable<int> ids) => ids.Distinct().Select(x => areaNames.GetValueOrDefault(x, $"#{x}")).OrderBy(x => x).ToArray();

        var package = new ProjectPackage { ExportedTs = ScadaTime.NowMs(), ExportedBy = exportedBy };
        void Add(string section, string key, int[] areaIds, JsonObject data)
        {
            if (include is not null && !include(section, areaIds)) { package.Partial = true; return; }
            package.Section(section).Add(new ProjectItem { Key = key, Areas = Names(areaIds), Data = data });
        }
        JsonObject Node(object entity, params string[] strip)
        {
            var node = JsonSerializer.SerializeToNode(entity, entity.GetType(), ProjectFormat.Json)!.AsObject();
            foreach (var name in strip.Append("id")) node.Remove(name);
            return node;
        }
        static void Ref(JsonObject node, string idField, string refField, IReadOnlyDictionary<int, string> names)
        {
            var value = node[idField]; node.Remove(idField);
            node[refField] = value is null ? null : names.TryGetValue(value.GetValue<int>(), out var name) ? name : $"#缺失:{value.GetValue<int>()}";
        }
        string Rewrite(string text) => ProjectFormat.IdReference.Replace(text, m => tagKeys.TryGetValue(int.Parse(m.Groups[1].Value), out var key) ? "#{" + key + "}" : m.Value);
        JsonArray Refs(string ids) => new(CalcScriptIds(ids).Select(id => (JsonNode?)JsonValue.Create(tagKeys.TryGetValue(id, out var key) ? key : $"#缺失:{id}")).ToArray());

        foreach (var area in areas) Add("areas", area.Name, [], Node(area));
        foreach (var role in roles) Add("roles", role.Name, [], Node(role));
        foreach (var device in devices)
        {
            var node = Node(device, "hasOpcPassword", "disabledSinceTs");
            Ref(node, "areaId", "areaRef", areaNames); Ref(node, "gatewayId", "gatewayRef", gateways);
            Add("devices", device.Name, [device.AreaId], node);
        }
        foreach (var tag in tags.Where(x => x.Kind != TagKind.Derived))
        {
            var node = Node(tag, "byteCount", "registerCount", "calculationRevision", "jobId", "jobOutput");
            Ref(node, "deviceId", "deviceRef", deviceNames); Ref(node, "areaId", "areaRef", areaNames);
            node["expression"] = Rewrite(tag.Expression);
            Add("tags", tagKeys[tag.Id], TagAreaIds(tag), node);
        }
        foreach (var job in jobs)
        {
            var node = Node(job, "revision", "updatedTs", "runAsUserId", "runAsUserName", "readTags", "writeTags");
            Ref(node, "areaId", "areaRef", areaNames); Ref(node, "inputTagId", "inputTagRef", tagKeys);
            node["script"] = Rewrite(job.Script);
            node["readTagRefs"] = Refs(job.ReadTags); node["writeTagRefs"] = Refs(job.WriteTags);
            node["unit"] = tags.FirstOrDefault(t => t.Kind == TagKind.Derived && t.JobId == job.Id && !FixedOutputs.Contains(t.JobOutput))?.Unit ?? "";
            Add("calcJobs", job.Name, [job.AreaId], node);
        }
        foreach (var rule in rules.OrderBy(x => x.Id))
        {
            var node = Node(rule, "outOfService", "outOfServiceReason");
            Ref(node, "tagId", "tagRef", tagKeys); Ref(node, "suppressTagId", "suppressTagRef", tagKeys);
            var tag = tags.FirstOrDefault(x => x.Id == rule.TagId);
            Add("alarmRules", $"{tagKeys.GetValueOrDefault(rule.TagId)}|{rule.Key}", tag is null ? [] : TagAreaIds(tag), node);
        }
        foreach (var report in reports)
        {
            var node = Node(report, "revision", "updatedTs", "scheduleSinceTs", "columnsJson");
            Ref(node, "areaId", "areaRef", areaNames); Ref(node, "emailChannelId", "emailChannelRef", channels);
            var columns = JsonNode.Parse(string.IsNullOrWhiteSpace(report.ColumnsJson) ? "[]" : report.ColumnsJson)!.AsArray();
            foreach (var column in columns.OfType<JsonObject>())
            {
                if (column.ContainsKey("tagId")) Ref(column, "tagId", "tagRef", tagKeys);
                if (column.ContainsKey("jobId")) Ref(column, "jobId", "jobRef", jobNames);
            }
            node["columns"] = columns;
            Add("reportTemplates", report.Name, [report.AreaId], node);
        }
        foreach (var template in templates)
            Add("objectTemplates", template.Name, [], new JsonObject
            {
                ["name"] = template.Name, ["draft"] = JsonNode.Parse(template.DraftJson),
                ["versions"] = new JsonArray(versions.Where(v => v.TemplateId == template.Id).Select(v => (JsonNode)new JsonObject { ["version"] = v.Version, ["document"] = JsonNode.Parse(v.DocumentJson) }).ToArray())
            });
        foreach (var screen in screens)
        {
            JsonNode? Doc(string? json) { if (json is null) return null; var node = JsonNode.Parse(json); ScreenRefs(node, tagKeys, screenNames); return node; }
            Add("screens", screen.Name, [], new JsonObject { ["name"] = screen.Name, ["draft"] = Doc(screen.DraftJson), ["published"] = Doc(screen.PublishedJson), ["publishedName"] = screen.PublishedName });
        }
        // 控制策略与顺控是工程设计的一部分，入包；挂牌、就地/远程、复核请求与运行记录是运行状态，不入包。
        string? Expr(string? text) => text is null ? null : Rewrite(text);
        foreach (var point in controlPoints)
        {
            var node = Node(point, "updatedTs", "updatedBy");
            Ref(node, "tagId", "tagRef", tagKeys);
            node["permitExpression"] = Expr(point.PermitExpression); node["feedbackExpression"] = Expr(point.FeedbackExpression);
            var tag = tags.FirstOrDefault(x => x.Id == point.TagId);
            Add("controlPoints", tagKeys.GetValueOrDefault(point.TagId) ?? $"#缺失:{point.TagId}", tag is null ? [] : TagAreaIds(tag), node);
        }
        foreach (var sequence in sequences)
        {
            var node = Node(sequence, "revision", "updatedTs", "updatedBy", "stepsJson");
            Ref(node, "areaId", "areaRef", areaNames);
            node["steps"] = new JsonArray(Scada.Engine.Control.ControlWorkflowService.Steps(sequence).Select(step =>
            {
                var s = Node(step); Ref(s, "tagId", "tagRef", tagKeys); s["confirmExpression"] = Expr(step.ConfirmExpression); return (JsonNode)s;
            }).ToArray());
            Add("controlSequences", sequence.Name, [sequence.AreaId], node);
        }
        // 配方模板连同其配方（各取当前版本的值）作为一项入包；版本历史与下发记录属于运行记录，不入包。
        foreach (var template in recipeTemplates)
        {
            var node = Node(template, "revision", "updatedTs", "updatedBy", "parametersJson");
            Ref(node, "areaId", "areaRef", areaNames);
            node["parameters"] = new JsonArray(Scada.Engine.Control.RecipeService.Parameters(template).Select(p => { var n = Node(p); Ref(n, "tagId", "tagRef", tagKeys); return (JsonNode)n; }).ToArray());
            node["recipes"] = new JsonArray(recipes.Where(x => x.TemplateId == template.Id).Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name, ["description"] = r.Description,
                ["values"] = new JsonArray((recipeVersions.TryGetValue((r.Id, r.CurrentVersion), out var v) ? Scada.Engine.Control.RecipeService.Values(v) : [])
                    .Select(x => (JsonNode)new JsonObject { ["key"] = x.Key, ["value"] = x.Value }).ToArray())
            }).ToArray());
            Add("recipeTemplates", template.Name, [template.AreaId], node);
        }
        foreach (var section in ProjectFormat.Sections) package.Section(section).Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return package;
    }

    internal static IEnumerable<int> CalcScriptIds(string ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x.TrimStart('#'), out var id) ? id : 0).Where(x => x > 0).Order();

    // 画面文档内 tagId/screenId → tagRef/screenRef（递归）。
    private static void ScreenRefs(JsonNode? node, IReadOnlyDictionary<int, string> tags, IReadOnlyDictionary<int, string> screens)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (field, target, refField) in new[] { ("tagId", tags, "tagRef"), ("screenId", screens, "screenRef") })
                    if (obj.ContainsKey(field))
                    {
                        var value = obj[field]; obj.Remove(field);
                        obj[refField] = value is null ? null : target.TryGetValue(value.GetValue<int>(), out var key) ? key : $"#缺失:{value.GetValue<int>()}";
                    }
                foreach (var child in obj.Select(x => x.Value).ToList()) ScreenRefs(child, tags, screens);
                break;
            case JsonArray array:
                foreach (var child in array) ScreenRefs(child, tags, screens);
                break;
        }
    }
}
