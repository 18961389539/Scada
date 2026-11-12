using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Scripting;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Engine.Reports;
using Scada.Server.Endpoints;
using Scada.Server.Services;

using ExpressionMap = System.Collections.Generic.Dictionary<Scada.Core.Entities.Tag, (Scada.Server.Projects.PlanEntry Entry, string Expression)>;

namespace Scada.Server.Projects;

/// <summary>
/// 一次工程应用的共享上下文：数据库、执行身份、查找表、错误/禁止/警告收集与权限判定。
/// 各 <see cref="ProjectSection"/> 复用这里的判据，避免每段各写一遍查找与权限逻辑。
/// </summary>
public sealed class ProjectApplyContext(
    ConfigDbContext db,
    CalcJobConfiguration jobs,
    AccessContext? access,
    int? userId,
    string userName,
    IDbContextFactory<RuntimeDbContext> runtimeFactory,
    Scada.Core.Options.ScadaSettings? settings = null,
    Scada.Core.Interfaces.IProtocolDriverAvailability? drivers = null)
{
    public static readonly JsonSerializerOptions ScreenJson = new(JsonSerializerDefaults.Web);

    public ConfigDbContext Db { get; } = db;
    public CalcJobConfiguration Jobs { get; } = jobs;
    public AccessContext? Access { get; } = access;
    public int? UserId { get; } = userId;
    public string UserName { get; } = userName;
    public IDbContextFactory<RuntimeDbContext> RuntimeFactory { get; } = runtimeFactory;
    public Scada.Core.Options.ScadaSettings? Settings { get; } = settings;
    public Scada.Core.Interfaces.IProtocolDriverAvailability? Drivers { get; } = drivers;

    public readonly long Now = ScadaTime.NowMs();

    public List<string> Errors { get; } = [];
    public List<string> Forbidden { get; } = [];
    public List<string> WarningList { get; } = [];
    public IReadOnlyList<string> Warnings => WarningList;
    public List<(int TagId, string Reason)> PendingAlarmCloses { get; } = [];

    internal Dictionary<string, int> AreaIdsByName { get; set; } = [];
    internal Dictionary<string, int> DeviceIdsByName { get; set; } = [];
    internal Dictionary<string, int> TagIdsByKey { get; set; } = [];
    internal Dictionary<string, int> JobIdsByName { get; set; } = [];
    internal Dictionary<string, int> ScreenIdsByName { get; set; } = [];
    internal Dictionary<string, int> GatewayIdsByName { get; set; } = [];
    internal Dictionary<string, int> ChannelIdsByName { get; set; } = [];

    private RuntimeDbContext? _runtime;

    // 只在 ApplyAsync 期间可用：删除段依赖它。写成带守卫的属性，
    // 让「在 ApplyAsync 之外调用」变成明确异常，而不是编译器推断不出来的空引用。
    internal RuntimeDbContext Runtime =>
        _runtime ?? throw new InvalidOperationException("ProjectApplier 的运行时库只在 ApplyAsync 期间可用");

    internal void AttachRuntime(RuntimeDbContext runtime) => _runtime = runtime;
    internal void DetachRuntime() => _runtime = null;

    internal void Error(PlanEntry entry, string message) => Errors.Add($"{ProjectFormat.SectionLabels[entry.Section]} {entry.FinalKey}：{message}");

    internal async Task SaveAsync(CancellationToken ct)
    {
        try { await Db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex)
        {
            Errors.Add($"数据库约束冲突（名称重复或仍被引用）：{ex.InnerException?.Message ?? ex.Message}");
            throw new ProjectApplyException(Errors, Forbidden);
        }
    }

    internal async Task LoadAsync(CancellationToken ct)
    {
        AreaIdsByName = await Db.Areas.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        var devices = await Db.Devices.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        DeviceIdsByName = devices.ToDictionary(x => x.Name, x => x.Id);
        var deviceNames = devices.ToDictionary(x => x.Id, x => x.Name);
        TagIdsByKey = (await Db.Tags.AsNoTracking().Select(x => new { x.Id, x.DeviceId, x.Name }).ToListAsync(ct))
            .ToDictionary(x => x.DeviceId is int d ? $"{deviceNames.GetValueOrDefault(d)}/{x.Name}" : $"@{x.Name}", x => x.Id);
        JobIdsByName = await Db.CalcJobs.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        ScreenIdsByName = await Db.ProcessScreens.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        GatewayIdsByName = await Db.Gateways.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        ChannelIdsByName = await Db.NotificationChannels.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
    }

    internal async Task<Tag?> FindTagAsync(string key, CancellationToken ct) => TagIdsByKey.TryGetValue(key, out var id) ? await Db.Tags.SingleOrDefaultAsync(x => x.Id == id, ct) : null;

    internal async Task<Dictionary<string, int>> RuleKeysAsync(CancellationToken ct)
    {
        var tagKeys = TagIdsByKey.ToDictionary(x => x.Value, x => x.Key);
        var result = new Dictionary<string, int>();
        foreach (var rule in await Db.AlarmRules.AsNoTracking().ToListAsync(ct)) result[$"{tagKeys.GetValueOrDefault(rule.TagId)}|{rule.Key}"] = rule.Id;
        return result;
    }

    // 把 xxxRef 换回数据库编号（xxxId）；无法解析的引用记为错误并返回 null。
    internal JsonObject? Resolve(PlanEntry entry, JsonObject source, bool allowMissingScreenRefs = false)
    {
        var data = (JsonObject)source.DeepClone(); var ok = true;
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var name in obj.Select(x => x.Key).ToList())
                    {
                        var value = obj[name];
                        if (ProjectFormat.RefTargets.ContainsKey(name))
                        {
                            // 包内引用用"名称键"表达，落到实体时按固定约定改成外键：
                            // xxxRef → xxxId（单个），xxxRefs → xxxs（集合，tagRefs 特例输出为 tags）。
                            obj.Remove(name);
                            if (name.EndsWith("Refs"))
                            {
                                var ids = new List<int>();
                                foreach (var key in value?.AsArray().Select(x => x?.GetValue<string>() ?? "") ?? [])
                                    if (Lookup(name, key) is int id) ids.Add(id); else { ok = false; Error(entry, $"引用的测点 {key} 不存在"); }
                                if (name == "tagRefs") obj["tags"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
                                else obj[name[..^4] + "s"] = string.Join(",", ids);
                            }
                            else
                            {
                                var key = value?.GetValue<string>();
                                int? id = key is null ? null : Lookup(name, key);
                                if (key is not null && id is null)
                                {
                                    if (name is "emailChannelRef") WarningList.Add($"{ProjectFormat.SectionLabels[entry.Section]} {entry.FinalKey}：本环境没有 {key}，已置空");
                                    else if (name == "screenRef" && allowMissingScreenRefs) WarningList.Add($"运行布局 {entry.FinalKey}：草稿引用的画面 {key} 不存在，已清空，请重新选择");
                                    else { ok = false; Error(entry, $"引用的{RefLabel(name)} {key} 不存在"); }
                                }
                                obj[name[..^3] + "Id"] = id;
                            }
                        }
                        else if (ProjectFormat.ExpressionFields.Contains(name) && value is JsonValue text && text.TryGetValue<string>(out var expression))
                        {
                            if (name is "script" or "publishedScript" && obj["kind"]?.GetValue<string>() == "CSharp") continue;
                            var resolved = ResolveExpression(entry, expression); if (resolved is null) ok = false; else obj[name] = resolved;
                        }
                        else if (!ProjectFormat.RefTargets.ContainsKey(name) && (name.EndsWith("Ref", StringComparison.Ordinal) || name.EndsWith("Refs", StringComparison.Ordinal)))
                        {
                            // 以 Ref/Refs 结尾却不是已知引用：说明导入端加了字段但没注册到 RefTargets。
                            // 继续当普通字段递归会原样留给反序列化，而实体上没有同名属性 → 引用被静默丢弃、外键落成默认值。
                            // 这类错误在运行期几乎没有征兆（导入"成功"但关联到错误对象），因此直接拒绝。
                            ok = false;
                            Error(entry, $"未知的引用字段 {name}，工程包与当前版本不兼容");
                        }
                        else Walk(value);
                    }
                    break;
                case JsonArray array: foreach (var child in array) Walk(child); break;
            }
        }
        Walk(data);
        return ok ? data : null;
    }

    internal int? Lookup(string field, string key) => ProjectFormat.RefTargets[field] switch
    {
        "areas" => AreaIdsByName.TryGetValue(key, out var a) ? a : null,
        "devices" => DeviceIdsByName.TryGetValue(key, out var d) ? d : null,
        "tags" => TagIdsByKey.TryGetValue(key, out var t) ? t : null,
        "calcJobs" => JobIdsByName.TryGetValue(key, out var j) ? j : null,
        "screens" => ScreenIdsByName.TryGetValue(key, out var s) ? s : null,
        _ => field == "gatewayRef" ? GatewayIdsByName.TryGetValue(key, out var g) ? g : null : ChannelIdsByName.TryGetValue(key, out var c) ? c : null
    };

    internal static string RefLabel(string field) => ProjectFormat.RefTargets[field] is string section ? ProjectFormat.SectionLabels[section] : "对象";

    internal string? ResolveExpression(PlanEntry entry, string expression)
    {
        var ok = true;
        var result = ProjectFormat.KeyReference.Replace(expression, m =>
        {
            if (TagIdsByKey.TryGetValue(m.Groups[1].Value, out var id)) return "#" + id;
            ok = false; Error(entry, $"表达式引用的测点 {m.Groups[1].Value} 不存在"); return m.Value;
        });
        return ok ? result : null;
    }

    internal void NeedGlobal(Permission permission, PlanEntry entry)
    {
        if (Access is not null && !Access.HasGlobal(permission)) Forbidden.Add($"{ProjectFormat.SectionLabels[entry.Section]} {entry.FinalKey}：需要全局{Label(permission)}权限");
    }

    internal void Need(Permission permission, PlanEntry entry, IEnumerable<int> areas)
    {
        if (Access is null) return;
        var list = areas.Distinct().ToArray();
        if (list.Length == 0) { NeedGlobal(permission, entry); return; }
        foreach (var area in list.Where(a => !Access.Has(permission, a)))
            Forbidden.Add($"{ProjectFormat.SectionLabels[entry.Section]} {entry.FinalKey}：无区域 #{area} 的{Label(permission)}权限");
    }

    internal static string Label(Permission permission) => permission switch { Permission.Configure => "配置", Permission.Control => "控制", Permission.ManageUsers => "用户管理", _ => permission.ToString() };

    internal async Task<int[]> AreasOfAsync(Tag tag, CancellationToken ct)
    {
        if (tag.DeviceId is int d) return [await Db.Devices.Where(x => x.Id == d).Select(x => x.AreaId).SingleOrDefaultAsync(ct)];
        if (tag.AreaId is int a) return [a];
        return tag.Id > 0 && Access is not null ? Access.Model.AreasOfTag(tag.Id) : []; // 无显式区域的新计算测点需全局权限
    }

    internal async Task<int[]> TagAreasAsync(int tagId, CancellationToken ct)
    {
        var tag = await Db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tagId, ct);
        return tag is null ? [] : await AreasOfAsync(tag, ct);
    }

    internal async Task<bool> CanTagAsync(Permission permission, int tagId, CancellationToken ct)
    {
        var areas = await TagAreasAsync(tagId, ct);
        return areas.Length == 0 ? Access!.HasGlobal(permission) : areas.All(a => Access!.Has(permission, a));
    }}
