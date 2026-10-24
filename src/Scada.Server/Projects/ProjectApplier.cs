using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Engine.Reports;
using Scada.Server.Endpoints;

namespace Scada.Server.Projects;

public sealed class ProjectApplyException(IReadOnlyList<string> errors, IReadOnlyList<string> forbidden)
    : Exception(forbidden.Count > 0 ? "权限不足：" + string.Join("；", forbidden.Take(5)) : "校验未通过：" + string.Join("；", errors.Take(5)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
    public IReadOnlyList<string> Forbidden { get; } = forbidden;
}

// 在调用方的事务内按依赖顺序执行计划（区域→角色→设备→测点→计算任务→计算表达式→报警规则→报表模板→对象模板→画面→删除）。
// 任一校验/权限问题都会累计并在结束时抛出，由调用方回滚整个事务，保证全部成功或全部不变。
public sealed class ProjectApplier(ConfigDbContext db, CalcJobConfiguration jobs, AccessContext? access, int? userId, string userName)
{
    private static readonly JsonSerializerOptions ScreenJson = new(JsonSerializerDefaults.Web);
    private readonly List<string> _errors = [], _forbidden = [];
    private Dictionary<string, int> _areas = [], _devices = [], _tags = [], _jobs = [], _screens = [], _gateways = [], _channels = [];
    private readonly long _now = ScadaTime.NowMs();

    public async Task ApplyAsync(ProjectPlan plan, CancellationToken ct)
    {
        var work = plan.Effective.ToList();
        IEnumerable<PlanEntry> Upserts(string section) => work.Where(x => x.Section == section && x.Action is "add" or "update" or "rename");
        IEnumerable<PlanEntry> Deletes(string section) => work.Where(x => x.Section == section && x.Action == "delete");
        await LoadAsync(ct);

        foreach (var entry in Upserts("areas"))
        {
            NeedGlobal(Permission.Configure, entry);
            var incoming = entry.Data!.Deserialize<Area>(ProjectFormat.Json)!;
            var area = entry.Action == "update" ? await db.Areas.SingleAsync(x => x.Name == entry.FinalKey, ct) : db.Areas.Add(new Area()).Entity;
            area.Name = incoming.Name.Trim(); area.Description = incoming.Description;
            if (area.Name.Length is 0 or > 100) Error(entry, "区域名称须为 1～100 个字符");
        }
        await SaveAsync(ct); await LoadAsync(ct);

        foreach (var entry in Upserts("roles"))
        {
            NeedGlobal(Permission.ManageUsers, entry);
            var incoming = entry.Data!.Deserialize<AccessRole>(ProjectFormat.Json)!;
            var role = entry.Action == "update" ? await db.AccessRoles.SingleAsync(x => x.Name == entry.FinalKey, ct) : db.AccessRoles.Add(new AccessRole()).Entity;
            role.Name = incoming.Name.Trim(); role.Description = incoming.Description; role.Permissions = incoming.Permissions;
        }
        await SaveAsync(ct);

        foreach (var entry in Upserts("devices"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var incoming = data.Deserialize<Device>(ProjectFormat.Json)!;
            var existing = entry.Action == "update" ? await db.Devices.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            Need(Permission.Configure, entry, [incoming.AreaId, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            if (ConfigurationValidator.Device(incoming) is string error) { Error(entry, error); continue; }
            if (existing is null)
            {
                incoming.DisabledSinceTs = incoming.Enabled ? 0 : _now; db.Devices.Add(incoming);
            }
            else
            {
                incoming.Id = existing.Id; incoming.OpcPasswordProtected = existing.OpcPasswordProtected;
                incoming.DisabledSinceTs = incoming.Enabled ? 0 : existing.Enabled ? _now : existing.DisabledSinceTs;
                db.Entry(existing).CurrentValues.SetValues(incoming);
            }
        }
        await SaveAsync(ct); await LoadAsync(ct);

        // 测点第一遍：计算测点先用占位表达式，待计算任务的派生测点生成后再写入真实表达式。
        var expressions = new Dictionary<Tag, (PlanEntry Entry, string Expression)>();
        foreach (var entry in Upserts("tags"))
        {
            var raw = (JsonObject)entry.Data!.DeepClone(); var expression = raw["expression"]?.GetValue<string>() ?? ""; raw["expression"] = "";
            var data = Resolve(entry, raw); if (data is null) continue;
            var incoming = data.Deserialize<Tag>(ProjectFormat.Json)!;
            if (incoming.Kind == TagKind.Derived) { Error(entry, "派生测点由计算任务维护，不能直接导入"); continue; }
            var existing = entry.Action == "update" ? await FindTagAsync(entry.FinalKey, ct) : null;
            if (existing is not null && existing.Kind != incoming.Kind) { Error(entry, "不能修改测点来源类型，请改名导入"); continue; }
            Need(Permission.Configure, entry, [.. await AreasOfAsync(incoming, ct), .. existing is null ? [] : await AreasOfAsync(existing, ct)]);
            if (incoming.Kind == TagKind.Calculated) incoming.Expression = "0";
            var device = incoming.DeviceId is int d ? await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == d, ct) : null;
            if (ConfigurationValidator.Tag(incoming, device) is string error) { Error(entry, error); continue; }
            Tag target;
            if (existing is null) { incoming.CalculationRevision = 1; db.Tags.Add(incoming); target = incoming; }
            else
            {
                incoming.Id = existing.Id; incoming.CalculationRevision = existing.CalculationRevision + (existing.Kind == TagKind.Calculated ? 1 : 0);
                incoming.Expression = existing.Expression;
                db.Entry(existing).CurrentValues.SetValues(incoming); target = existing;
            }
            if (incoming.Kind == TagKind.Calculated) expressions[target] = (entry, expression);
        }
        await SaveAsync(ct); await LoadAsync(ct);

        foreach (var entry in Upserts("calcJobs"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var unit = data["unit"]?.GetValue<string>(); data.Remove("unit");
            var candidate = data.Deserialize<CalcJob>(ProjectFormat.Json)!;
            var existing = entry.Action == "update" ? await db.CalcJobs.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            Need(Permission.Configure, entry, [candidate.AreaId, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            if (existing is not null && existing.Kind != candidate.Kind) { Error(entry, "不能修改任务类型，请改名导入"); continue; }
            candidate.Id = existing?.Id ?? 0;
            IReadOnlyList<string> outputs;
            try { outputs = await jobs.ValidateAsync(db, candidate, ct); }
            catch (CalculationRequestException ex) { Error(entry, ex.Message); continue; }
            var writes = ProjectExporter.CalcScriptIds(candidate.WriteTags).ToArray();
            foreach (var tagId in writes)
                if (access is not null && !await CanTagAsync(Permission.Control, tagId, ct)) _forbidden.Add($"计算任务 {entry.FinalKey}：无权控制可写测点 #{tagId}");
            CalcJob job;
            if (existing is null) { db.CalcJobs.Add(candidate); job = candidate; job.Revision = 1; }
            else { var revision = existing.Revision + 1; db.Entry(existing).CurrentValues.SetValues(candidate); job = existing; job.Revision = revision; }
            job.UpdatedTs = _now;
            if (job.Kind == CalcJobKind.Script && writes.Length > 0) { job.RunAsUserId = userId; job.RunAsUserName = userName; }
            else { job.RunAsUserId = null; job.RunAsUserName = ""; }
            await SaveAsync(ct);
            try { await CalcJobConfiguration.SyncOutputsAsync(db, job, outputs, unit, ct); }
            catch (CalculationRequestException ex) { Error(entry, ex.Message); }
        }
        await SaveAsync(ct); await LoadAsync(ct);

        foreach (var (tag, (entry, expression)) in expressions)
        {
            var resolved = ResolveExpression(entry, expression); if (resolved is null) continue;
            tag.Expression = resolved;
            if (ConfigurationValidator.Tag(tag) is string error) Error(entry, error);
        }
        await SaveAsync(ct);

        var rules = await RuleKeysAsync(ct);
        foreach (var entry in Upserts("alarmRules"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var incoming = data.Deserialize<AlarmRule>(ProjectFormat.Json)!;
            var existing = entry.Action == "update" && rules.TryGetValue(entry.FinalKey, out var id) ? await db.AlarmRules.SingleAsync(x => x.Id == id, ct) : null;
            Need(Permission.Configure, entry, [.. await TagAreasAsync(incoming.TagId, ct), .. existing is null ? [] : await TagAreasAsync(existing.TagId, ct)]);
            if (EndpointHelpers.ValidateAlarmRule(incoming) is string error) { Error(entry, error); continue; }
            if (incoming.Key.Length is 0 or > 64) { Error(entry, "报警规则稳定键须为 1～64 个字符"); continue; }
            if (existing is null) db.AlarmRules.Add(incoming);
            else { incoming.Id = existing.Id; incoming.OutOfService = existing.OutOfService; incoming.OutOfServiceReason = existing.OutOfServiceReason; db.Entry(existing).CurrentValues.SetValues(incoming); }
        }
        await SaveAsync(ct);

        foreach (var entry in Upserts("reportTemplates"))
        {
            var data = Resolve(entry, entry.Data!); if (data is null) continue;
            var columns = data["columns"]?.AsArray() ?? []; data.Remove("columns");
            var incoming = data.Deserialize<ReportTemplate>(ProjectFormat.Json)!;
            incoming.ColumnsJson = columns.ToJsonString();
            var existing = entry.Action == "update" ? await db.ReportTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct) : null;
            Need(Permission.Configure, entry, [incoming.AreaId, .. existing is null ? Array.Empty<int>() : [existing.AreaId]]);
            try { ReportTemplates.Validate(incoming, ReportTemplates.Columns(incoming.ColumnsJson)); }
            catch (Exception ex) when (ex is ReportTemplateException or JsonException) { Error(entry, ex.Message); continue; }
            if (existing is null) { incoming.Revision = 1; incoming.UpdatedTs = _now; incoming.ScheduleSinceTs = incoming.ScheduleEnabled ? _now : null; db.ReportTemplates.Add(incoming); }
            else
            {
                incoming.Id = existing.Id; incoming.Revision = existing.Revision + 1; incoming.UpdatedTs = _now;
                incoming.ScheduleSinceTs = incoming.ScheduleEnabled ? existing.ScheduleEnabled ? existing.ScheduleSinceTs : _now : existing.ScheduleSinceTs;
                db.Entry(existing).CurrentValues.SetValues(incoming);
            }
        }
        await SaveAsync(ct);

        foreach (var entry in Upserts("objectTemplates"))
        {
            NeedGlobal(Permission.Configure, entry);
            var draft = entry.Data!["draft"]?.ToJsonString() ?? "{}";
            try { _ = JsonSerializer.Deserialize<ObjectTemplateDocument>(draft, ObjectTemplateJson.Options) ?? throw new JsonException("内容为空"); }
            catch (JsonException ex) { Error(entry, $"模板内容无效：{ex.Message}"); continue; }
            var template = entry.Action == "update" ? await db.ObjectTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct) : db.ObjectTemplates.Add(new DeviceObjectTemplate()).Entity;
            template.Name = ProjectFormat.Name(entry.Data!); template.DraftJson = draft;
            template.Revision = entry.Action == "update" ? template.Revision + 1 : 1; template.UpdatedTs = _now; template.UpdatedBy = userName;
            await SaveAsync(ct);
            var versions = await db.ObjectTemplateVersions.Where(x => x.TemplateId == template.Id).ToDictionaryAsync(x => x.Version, ct);
            foreach (var version in entry.Data!["versions"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var number = version["version"]!.GetValue<int>(); var document = version["document"]?.ToJsonString() ?? "{}";
                if (versions.TryGetValue(number, out var stored))
                {
                    if (!JsonNode.DeepEquals(JsonNode.Parse(stored.DocumentJson), JsonNode.Parse(document))) Error(entry, $"已发布版本 v{number} 内容不同，已发布版本不可覆盖，请改名导入");
                }
                else db.ObjectTemplateVersions.Add(new DeviceObjectTemplateVersion { TemplateId = template.Id, Version = number, DocumentJson = document, CreatedTs = _now, CreatedBy = userName });
            }
            template.LatestVersion = Math.Max(template.LatestVersion, entry.Data!["versions"]?.AsArray().OfType<JsonObject>().Select(x => x["version"]!.GetValue<int>()).DefaultIfEmpty(0).Max() ?? 0);
        }
        await SaveAsync(ct);

        // 画面：先建出所有新画面以便互相跳转引用，再写入内容。
        var screenEntries = Upserts("screens").ToList();
        var screenRows = new Dictionary<PlanEntry, ProcessScreen>();
        foreach (var entry in screenEntries)
        {
            NeedGlobal(Permission.Configure, entry);
            var screen = entry.Action == "update" ? await db.ProcessScreens.SingleAsync(x => x.Name == entry.FinalKey, ct)
                : db.ProcessScreens.Add(new ProcessScreen { DraftJson = "{}", Revision = 0 }).Entity;
            screen.Name = ProjectFormat.Name(entry.Data!); screenRows[entry] = screen;
        }
        await SaveAsync(ct); await LoadAsync(ct);
        foreach (var (entry, screen) in screenRows)
        {
            string? Document(string field)
            {
                if (entry.Data![field] is not JsonNode node) return null;
                var data = Resolve(entry, new JsonObject { ["doc"] = node.DeepClone() }); if (data is null) return null;
                ScreenDocument document;
                try { document = data["doc"].Deserialize<ScreenDocument>(ScreenJson)!; }
                catch (JsonException ex) { Error(entry, $"画面内容无效：{ex.Message}"); return null; }
                foreach (var issue in ScreenValidator.Validate(document)) Error(entry, $"节点 {issue.NodeId}：{issue.Message}");
                return JsonSerializer.Serialize(document, ScreenJson);
            }
            screen.DraftJson = Document("draft") ?? JsonSerializer.Serialize(new ScreenDocument(), ScreenJson);
            screen.PublishedJson = Document("published");
            screen.PublishedName = screen.PublishedJson is null ? null : entry.Data!["publishedName"]?.GetValue<string>() ?? screen.Name;
            screen.Revision++; screen.PublishedRevision = screen.PublishedJson is null ? null : screen.Revision;
            screen.UpdatedTs = _now; screen.UpdatedBy = userName;
        }
        await SaveAsync(ct);

        // 删除（仅回滚/镜像）：按依赖逆序。
        foreach (var entry in Deletes("screens"))
        {
            NeedGlobal(Permission.Configure, entry);
            var screen = await db.ProcessScreens.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (await db.ObjectInstances.AnyAsync(x => x.ScreenId == screen.Id, ct)) { Error(entry, "画面仍属于模板对象，不能删除"); continue; }
            db.ProcessScreens.Remove(screen);
        }
        foreach (var entry in Deletes("reportTemplates"))
        {
            var report = await db.ReportTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct);
            Need(Permission.Configure, entry, [report.AreaId]); db.ReportTemplates.Remove(report); // 已生成报表保留
        }
        foreach (var entry in Deletes("alarmRules"))
        {
            if (!rules.TryGetValue(entry.FinalKey, out var id)) continue;
            var rule = await db.AlarmRules.SingleAsync(x => x.Id == id, ct);
            Need(Permission.Configure, entry, await TagAreasAsync(rule.TagId, ct)); db.AlarmRules.Remove(rule);
        }
        await SaveAsync(ct);
        foreach (var entry in Deletes("calcJobs"))
        {
            var job = await db.CalcJobs.SingleAsync(x => x.Name == entry.FinalKey, ct);
            Need(Permission.Configure, entry, [job.AreaId]);
            try { await CalcJobConfiguration.SyncOutputsAsync(db, job, [], null, ct); db.CalcJobs.Remove(job); }
            catch (CalculationRequestException ex) { Error(entry, ex.Message); }
        }
        await SaveAsync(ct);
        foreach (var entry in Deletes("tags").OrderBy(x => x.FinalKey.StartsWith('@') ? 0 : 1))
        {
            var tag = await FindTagAsync(entry.FinalKey, ct); if (tag is null) continue;
            Need(Permission.Configure, entry, await AreasOfAsync(tag, ct));
            await db.AlarmEvents.Where(x => x.TagId == tag.Id && x.EndTs == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.EndTs, _now).SetProperty(x => x.EndReason, "工程回滚删除测点"), ct);
            await db.TagDependencies.Where(x => x.TagId == tag.Id).ExecuteDeleteAsync(ct);
            db.Tags.Remove(tag);
        }
        await SaveAsync(ct);
        foreach (var entry in Deletes("devices"))
        {
            var device = await db.Devices.SingleAsync(x => x.Name == entry.FinalKey, ct);
            Need(Permission.Configure, entry, [device.AreaId]);
            if (await db.ObjectInstances.AnyAsync(x => x.DeviceId == device.Id, ct)) { Error(entry, "设备仍属于模板对象，不能删除"); continue; }
            if (await db.Tags.AnyAsync(x => x.DeviceId == device.Id, ct)) { Error(entry, "设备下仍有测点"); continue; }
            db.Devices.Remove(device);
        }
        foreach (var entry in Deletes("objectTemplates"))
        {
            NeedGlobal(Permission.Configure, entry);
            var template = await db.ObjectTemplates.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (await db.ObjectInstances.AnyAsync(x => x.TemplateId == template.Id, ct)) { Error(entry, "模板仍有对象实例，不能删除"); continue; }
            db.ObjectTemplates.Remove(template);
        }
        foreach (var entry in Deletes("roles"))
        {
            NeedGlobal(Permission.ManageUsers, entry);
            var role = await db.AccessRoles.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (await db.UserRoleAssignments.AnyAsync(x => x.RoleId == role.Id, ct)) { Error(entry, "角色仍分配给用户，不能删除"); continue; }
            db.AccessRoles.Remove(role);
        }
        await SaveAsync(ct);
        foreach (var entry in Deletes("areas"))
        {
            NeedGlobal(Permission.Configure, entry);
            var area = await db.Areas.SingleAsync(x => x.Name == entry.FinalKey, ct);
            if (area.Id == Area.DefaultId) { Error(entry, "默认区域不能删除"); continue; }
            if (await db.Devices.AnyAsync(x => x.AreaId == area.Id, ct) || await db.Tags.AnyAsync(x => x.AreaId == area.Id, ct) || await db.CalcJobs.AnyAsync(x => x.AreaId == area.Id, ct)
                || await db.ReportTemplates.AnyAsync(x => x.AreaId == area.Id, ct) || await db.UserRoleAssignments.AnyAsync(x => x.AreaId == area.Id, ct) || await db.NotificationRoutes.AnyAsync(x => x.AreaId == area.Id, ct))
            { Error(entry, "区域仍被设备、测点、计算任务、报表模板、用户授权或通知路由使用"); continue; }
            db.Areas.Remove(area);
        }
        await SaveAsync(ct);

        // 计算依赖图整体校验（循环、缺失引用、层数），并重建依赖表。
        var all = await db.Tags.AsNoTracking().ToListAsync(ct);
        try
        {
            var graph = CalculationGraph.Compile(all);
            await db.TagDependencies.ExecuteDeleteAsync(ct);
            db.TagDependencies.AddRange(graph.SelectMany(n => n.Expression.Dependencies.Select(input => new TagDependency { TagId = n.Tag.Id, InputTagId = input })));
            await SaveAsync(ct);
        }
        catch (TagExpressionException ex) { _errors.Add($"计算测点：{ex.Message}"); }
        if (_errors.Count > 0 || _forbidden.Count > 0) throw new ProjectApplyException(_errors, _forbidden.Distinct().ToList());
    }

    private void Error(PlanEntry entry, string message) => _errors.Add($"{ProjectFormat.SectionLabels[entry.Section]} {entry.FinalKey}：{message}");

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex)
        {
            _errors.Add($"数据库约束冲突（名称重复或仍被引用）：{ex.InnerException?.Message ?? ex.Message}");
            throw new ProjectApplyException(_errors, _forbidden);
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        _areas = await db.Areas.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        var devices = await db.Devices.AsNoTracking().Select(x => new { x.Id, x.Name }).ToListAsync(ct);
        _devices = devices.ToDictionary(x => x.Name, x => x.Id);
        var deviceNames = devices.ToDictionary(x => x.Id, x => x.Name);
        _tags = (await db.Tags.AsNoTracking().Select(x => new { x.Id, x.DeviceId, x.Name }).ToListAsync(ct))
            .ToDictionary(x => x.DeviceId is int d ? $"{deviceNames.GetValueOrDefault(d)}/{x.Name}" : $"@{x.Name}", x => x.Id);
        _jobs = await db.CalcJobs.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        _screens = await db.ProcessScreens.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        _gateways = await db.Gateways.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        _channels = await db.NotificationChannels.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
    }

    private async Task<Tag?> FindTagAsync(string key, CancellationToken ct) => _tags.TryGetValue(key, out var id) ? await db.Tags.SingleOrDefaultAsync(x => x.Id == id, ct) : null;

    private async Task<Dictionary<string, int>> RuleKeysAsync(CancellationToken ct)
    {
        var tagKeys = _tags.ToDictionary(x => x.Value, x => x.Key);
        var result = new Dictionary<string, int>();
        foreach (var rule in await db.AlarmRules.AsNoTracking().ToListAsync(ct)) result[$"{tagKeys.GetValueOrDefault(rule.TagId)}|{rule.Key}"] = rule.Id;
        return result;
    }

    // 把 xxxRef 换回数据库编号（xxxId）；无法解析的引用记为错误并返回 null。
    private JsonObject? Resolve(PlanEntry entry, JsonObject source)
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
                            obj.Remove(name);
                            if (name.EndsWith("Refs"))
                            {
                                var ids = new List<int>();
                                foreach (var key in value?.AsArray().Select(x => x?.GetValue<string>() ?? "") ?? [])
                                    if (Lookup(name, key) is int id) ids.Add(id); else { ok = false; Error(entry, $"引用的测点 {key} 不存在"); }
                                obj[name[..^4] + "s"] = string.Join(",", ids);
                            }
                            else
                            {
                                var key = value?.GetValue<string>();
                                int? id = key is null ? null : Lookup(name, key);
                                if (key is not null && id is null)
                                {
                                    if (name is "gatewayRef" or "emailChannelRef") _warnings.Add($"{ProjectFormat.SectionLabels[entry.Section]} {entry.FinalKey}：本环境没有 {key}，已置空");
                                    else { ok = false; Error(entry, $"引用的{RefLabel(name)} {key} 不存在"); }
                                }
                                obj[name[..^3] + "Id"] = id;
                            }
                        }
                        else if (ProjectFormat.ExpressionFields.Contains(name) && value is JsonValue text && text.TryGetValue<string>(out var expression))
                        {
                            var resolved = ResolveExpression(entry, expression); if (resolved is null) ok = false; else obj[name] = resolved;
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

    private readonly List<string> _warnings = [];
    public IReadOnlyList<string> Warnings => _warnings;

    private int? Lookup(string field, string key) => ProjectFormat.RefTargets[field] switch
    {
        "areas" => _areas.TryGetValue(key, out var a) ? a : null,
        "devices" => _devices.TryGetValue(key, out var d) ? d : null,
        "tags" => _tags.TryGetValue(key, out var t) ? t : null,
        "calcJobs" => _jobs.TryGetValue(key, out var j) ? j : null,
        "screens" => _screens.TryGetValue(key, out var s) ? s : null,
        _ => field == "gatewayRef" ? _gateways.TryGetValue(key, out var g) ? g : null : _channels.TryGetValue(key, out var c) ? c : null
    };

    private static string RefLabel(string field) => ProjectFormat.RefTargets[field] is string section ? ProjectFormat.SectionLabels[section] : "对象";

    private string? ResolveExpression(PlanEntry entry, string expression)
    {
        var ok = true;
        var result = ProjectFormat.KeyReference.Replace(expression, m =>
        {
            if (_tags.TryGetValue(m.Groups[1].Value, out var id)) return "#" + id;
            ok = false; Error(entry, $"表达式引用的测点 {m.Groups[1].Value} 不存在"); return m.Value;
        });
        return ok ? result : null;
    }

    private void NeedGlobal(Permission permission, PlanEntry entry)
    {
        if (access is not null && !access.HasGlobal(permission)) _forbidden.Add($"{ProjectFormat.SectionLabels[entry.Section]} {entry.FinalKey}：需要全局{Label(permission)}权限");
    }

    private void Need(Permission permission, PlanEntry entry, IEnumerable<int> areas)
    {
        if (access is null) return;
        var list = areas.Distinct().ToArray();
        if (list.Length == 0) { NeedGlobal(permission, entry); return; }
        foreach (var area in list.Where(a => !access.Has(permission, a)))
            _forbidden.Add($"{ProjectFormat.SectionLabels[entry.Section]} {entry.FinalKey}：无区域 #{area} 的{Label(permission)}权限");
    }

    private static string Label(Permission permission) => permission switch { Permission.Configure => "配置", Permission.Control => "控制", Permission.ManageUsers => "用户管理", _ => permission.ToString() };

    private async Task<int[]> AreasOfAsync(Tag tag, CancellationToken ct)
    {
        if (tag.DeviceId is int d) return [await db.Devices.Where(x => x.Id == d).Select(x => x.AreaId).SingleOrDefaultAsync(ct)];
        if (tag.AreaId is int a) return [a];
        return tag.Id > 0 && access is not null ? access.Model.AreasOfTag(tag.Id) : []; // 无显式区域的新计算测点需全局权限
    }

    private async Task<int[]> TagAreasAsync(int tagId, CancellationToken ct)
    {
        var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == tagId, ct);
        return tag is null ? [] : await AreasOfAsync(tag, ct);
    }

    private async Task<bool> CanTagAsync(Permission permission, int tagId, CancellationToken ct)
    {
        var areas = await TagAreasAsync(tagId, ct);
        return areas.Length == 0 ? access!.HasGlobal(permission) : areas.All(a => access!.Has(permission, a));
    }
}
