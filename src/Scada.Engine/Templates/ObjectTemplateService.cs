using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Templates;

public sealed record ObjectBatchRequest(int Version, List<ObjectInstanceParameters> Instances);
public sealed record ObjectUpgradeRequest(int Version, int Revision, string? ExpectedState, Dictionary<string, string>? Resolutions);
public sealed record ObjectUpgradeItem(int Id, int Version, int Revision, string ExpectedState, Dictionary<string, string>? Resolutions);
public sealed record ObjectUpgradeBatchRequest(List<ObjectUpgradeItem> Items);
public sealed record ObjectPlanPreview(int? InstanceId, string Name, int Version, int Revision, string State,
    int TagCount, int AlarmCount, int PanelNodeCount, IReadOnlyList<TemplateChange> Changes,
    IReadOnlyList<TemplateConflict> Conflicts, IReadOnlyList<string> Errors)
{
    public bool CanApply => Errors.Count == 0 && Conflicts.Count == 0;
}
public sealed record ObjectBatchPreview(IReadOnlyList<ObjectPlanPreview> Plans, IReadOnlyList<string> Errors)
{
    public bool CanApply => Errors.Count == 0 && Plans.Count > 0 && Plans.All(x => x.CanApply);
}
public sealed class TemplateOperationException(string message, int status = 400, object? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public object? Details { get; } = details;
}

public sealed class ObjectTemplateService(IDbContextFactory<ConfigDbContext> factory)
{
    public async Task<ObjectBatchPreview> PreviewCreateAsync(int templateId, ObjectBatchRequest request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var plans = await BuildCreateAsync(db, templateId, request, ct);
        return new(plans.Select(x => x.Preview()).ToList(), BatchErrors(plans));
    }

    public async Task<IReadOnlyList<object>> CreateAsync(int templateId, ObjectBatchRequest request, int? userId, string userName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var plans = await BuildCreateAsync(db, templateId, request, ct);
        RequireValid(plans);
        var result = new List<object>();
        foreach (var plan in plans) { result.Add(await ApplyAsync(db, plan, userName, ct)); db.ChangeTracker.Clear(); }
        Audit(db, userId, userName, $"实例化模板 #{templateId}：{plans.Count} 个对象");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return result;
    }

    public async Task<ObjectPlanPreview> PreviewUpgradeAsync(int id, ObjectUpgradeRequest request, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await BuildUpgradeAsync(db, id, request, ct)).Preview();
    }

    public async Task<IReadOnlyList<object>> UpgradeAsync(ObjectUpgradeBatchRequest request, int? userId, string userName, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count is < 1 or > 100 || request.Items.Any(x => x is null) || request.Items.Select(x => x.Id).Distinct().Count() != request.Items.Count)
            throw new TemplateOperationException("每次升级须选择 1～100 个不重复的对象");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var plans = new List<Plan>();
        foreach (var item in request.Items)
        {
            var plan = await BuildUpgradeAsync(db, item.Id, new(item.Version, item.Revision, item.ExpectedState, item.Resolutions), ct);
            if (string.IsNullOrWhiteSpace(item.ExpectedState) || item.ExpectedState != plan.State)
                throw new TemplateOperationException("现场配置自预览后已变化，请重新预览升级", 409);
            plans.Add(plan);
        }
        RequireValid(plans);
        var result = new List<object>();
        foreach (var plan in plans) { result.Add(await ApplyAsync(db, plan, userName, ct)); db.ChangeTracker.Clear(); }
        Audit(db, userId, userName, $"升级设备对象：{plans.Count} 个，按预览合并模板版本");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return result;
    }

    private static async Task<List<Plan>> BuildCreateAsync(ConfigDbContext db, int templateId, ObjectBatchRequest request, CancellationToken ct)
    {
        if (request.Instances is null || request.Instances.Count is < 1 or > 100 || request.Instances.Any(x => x is null))
            throw new TemplateOperationException("每次须创建 1～100 个对象");
        var definition = await DefinitionAsync(db, templateId, request.Version, ct);
        var plans = new List<Plan>();
        foreach (var parameters in request.Instances)
        {
            var plan = new Plan(templateId, request.Version, parameters, definition);
            await CheckParametersAsync(db, plan, ct);
            GenerateTags(plan);
            GenerateAlarmsAndPanel(plan);
            plan.Tags = ObjectTemplateJson.Copy(plan.DesiredTags);
            plan.Alarms = ObjectTemplateJson.Copy(plan.DesiredAlarms);
            plan.Panel = ObjectTemplateJson.Copy(plan.DesiredPanel);
            plan.Changes.Add(new("object", "create", $"生成 {plan.Tags.Count} 个测点、{plan.Alarms.Count} 条报警和设备面板草稿"));
            await ValidatePlanAsync(db, plan, ct);
            plans.Add(plan);
        }
        return plans;
    }

    private static async Task<Plan> BuildUpgradeAsync(ConfigDbContext db, int id, ObjectUpgradeRequest request, CancellationToken ct)
    {
        var instance = await db.ObjectInstances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new TemplateOperationException("设备对象不存在", 404);
        if (instance.Revision != request.Revision) throw new TemplateOperationException("对象版本已变化，请重新打开并预览", 409);
        if (request.Version <= instance.AppliedVersion) throw new TemplateOperationException("请选择高于当前应用版本的模板版本");
        var definition = await DefinitionAsync(db, instance.TemplateId, request.Version, ct);
        var baseline = ObjectTemplateJson.Read<ObjectInstanceBaseline>(instance.BaselineJson);
        var plan = new Plan(instance.TemplateId, request.Version, new(instance.Name, instance.DeviceId, instance.BaseAddress), definition)
        { Instance = instance, Resolutions = request.Resolutions ?? [] };
        await CheckParametersAsync(db, plan, ct);
        var tagIds = baseline.Tags.Values.Select(x => x.Id).ToList();
        var alarmIds = baseline.Alarms.Values.Select(x => x.Id).ToList();
        var localTags = await db.Tags.AsNoTracking().Where(x => tagIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
        var localAlarms = await db.AlarmRules.AsNoTracking().Where(x => alarmIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
        plan.ExistingTags = localTags.ToDictionary(x => x.Id); plan.ExistingAlarmIds = localAlarms.Select(x => x.Id).ToHashSet();
        plan.Screen = await db.ProcessScreens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == instance.ScreenId, ct)
            ?? throw new TemplateOperationException("对象面板不存在，请先检查工程配置", 409);
        GenerateTags(plan, baseline.Tags);
        plan.Tags = MergeItems("tag", baseline.Tags, localTags.ToDictionary(x => x.Id), plan.DesiredTags, x => x.Id,
            plan.Resolutions, plan.Conflicts, plan.Changes, plan.DeleteTagIds);
        GenerateAlarmsAndPanel(plan, baseline.Alarms);
        plan.Alarms = MergeItems("alarm", baseline.Alarms, localAlarms.ToDictionary(x => x.Id), plan.DesiredAlarms, x => x.Id,
            plan.Resolutions, plan.Conflicts, plan.Changes, plan.DeleteAlarmIds);
        var localPanel = ObjectTemplateJson.Read<ScreenDocument>(plan.Screen.DraftJson);
        plan.Panel = TemplateMerge.Whole("panel.document", "面板草稿与新版模板均有修改", baseline.Panel, localPanel, plan.DesiredPanel, plan.Resolutions, plan.Conflicts);
        plan.PanelName = TemplateMerge.Whole("panel.name", "面板名称", baseline.ScreenName, plan.Screen.Name, plan.DesiredPanelName, plan.Resolutions, plan.Conflicts);
        if (!ObjectTemplateJson.Equal(plan.Panel, localPanel) || plan.PanelName != plan.Screen.Name)
            plan.Changes.Add(new("panel", "update", "更新面板草稿，已发布的运行版本保持不变"));
        await ValidatePlanAsync(db, plan, ct);
        var referenceVersions = await db.ProcessScreens.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Revision, x.PublishedRevision }).ToListAsync(ct);
        var foreignAlarmRefs = await db.AlarmRules.AsNoTracking().Where(x => tagIds.Contains(x.TagId) && !alarmIds.Contains(x.Id))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.TagId }).ToListAsync(ct);
        plan.State = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ObjectTemplateJson.Write(new
        { instance, localTags, localAlarms, plan.Screen, referenceVersions, foreignAlarmRefs, request.Version }))));
        return plan;
    }

    private static Dictionary<string, T> MergeItems<T>(string kind, Dictionary<string, T> baseline, Dictionary<int, T> local,
        Dictionary<string, T> desired, Func<T, int> id, Dictionary<string, string> resolutions, List<TemplateConflict> conflicts,
        List<TemplateChange> changes, List<int> deleteIds) where T : class
    {
        var result = new Dictionary<string, T>();
        foreach (var (key, incoming) in desired)
        {
            if (!baseline.TryGetValue(key, out var before)) { result[key] = ObjectTemplateJson.Copy(incoming); changes.Add(new($"{kind}.{key}", "create", "新增模板成员")); continue; }
            if (!local.TryGetValue(id(before), out var current))
            {
                var conflictKey = $"{kind}.{key}.$missing";
                if (resolutions.GetValueOrDefault(conflictKey) != "useTemplate")
                    conflicts.Add(new(conflictKey, $"{kind}.{key} 已在本地删除，需要重新生成", ObjectTemplateJson.Write(before), "已删除", ObjectTemplateJson.Write(incoming), ["useTemplate"]));
                result[key] = ObjectTemplateJson.Copy(incoming);
                changes.Add(new($"{kind}.{key}", "create", "重新生成本地缺失成员"));
                continue;
            }
            result[key] = TemplateMerge.Fields($"{kind}.{key}", before, current, incoming, resolutions, conflicts);
            if (!ObjectTemplateJson.Equal(result[key], current)) changes.Add(new($"{kind}.{key}", "update", "合并新版模板字段，保留未冲突的本地覆盖"));
        }
        foreach (var (key, before) in baseline.Where(x => !desired.ContainsKey(x.Key)))
        {
            if (!local.TryGetValue(id(before), out var current)) continue;
            var conflictKey = $"{kind}.{key}.$remove";
            var resolution = resolutions.GetValueOrDefault(conflictKey);
            if (resolution is not ("keepLocal" or "useTemplate"))
                conflicts.Add(new(conflictKey, $"模板移除 {kind}.{key}，选择保留为独立配置或删除", ObjectTemplateJson.Write(before), ObjectTemplateJson.Write(current), "移除", ["keepLocal", "useTemplate"]));
            else if (resolution == "useTemplate") deleteIds.Add(id(current));
            changes.Add(new($"{kind}.{key}", resolution == "useTemplate" ? "delete" : "detach", resolution == "useTemplate" ? "删除并关闭相关活动报警" : "解除模板跟踪，保留现场配置"));
        }
        return result;
    }

    private static async Task<ObjectTemplateDocument> DefinitionAsync(ConfigDbContext db, int templateId, int version, CancellationToken ct)
    {
        var row = await db.ObjectTemplateVersions.AsNoTracking().FirstOrDefaultAsync(x => x.TemplateId == templateId && x.Version == version, ct)
            ?? throw new TemplateOperationException("模板发布版本不存在", 404);
        return ObjectTemplateJson.Read<ObjectTemplateDocument>(row.DocumentJson);
    }

    private static async Task CheckParametersAsync(ConfigDbContext db, Plan plan, CancellationToken ct)
    {
        var p = plan.Parameters;
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name != p.Name.Trim() || p.Name.Length > 40) plan.Errors.Add("对象名称须为 1～40 个字符且首尾无空格");
        if (p.BaseAddress is < 0 or > 65535) plan.Errors.Add("基址须在 0～65535 之间");
        if (!await db.Devices.AnyAsync(x => x.Id == p.DeviceId, ct)) plan.Errors.Add("采集设备不存在");
        var existingId = plan.Instance?.Id ?? 0;
        if (await db.ObjectInstances.AnyAsync(x => x.DeviceId == p.DeviceId && x.Name == p.Name && x.Id != existingId, ct)) plan.Errors.Add("此采集设备下已存在同名对象");
    }

    private static void GenerateTags(Plan plan, Dictionary<string, Tag>? baseline = null)
    {
        var virtualId = int.MaxValue;
        foreach (var item in plan.Definition.Tags)
        {
            var tag = ObjectTemplateJson.Copy(item.Properties);
            tag.Id = baseline?.GetValueOrDefault(item.Key)?.Id ?? virtualId--;
            tag.DeviceId = plan.Parameters.DeviceId;
            tag.Name = $"{plan.Parameters.Name}.{item.Properties.Name}";
            var address = (long)plan.Parameters.BaseAddress + item.AddressOffset;
            if (address > 65535 || address < 0) plan.Errors.Add($"{item.Key} 基址加偏移超出地址范围");
            tag.Address = (int)Math.Clamp(address, int.MinValue, int.MaxValue);
            plan.DesiredTags[item.Key] = tag;
        }
    }

    private static void GenerateAlarmsAndPanel(Plan plan, Dictionary<string, AlarmRule>? baseline = null)
    {
        var virtualId = int.MaxValue;
        foreach (var item in plan.Definition.Alarms)
        {
            var alarm = ObjectTemplateJson.Copy(item.Properties);
            alarm.Id = baseline?.GetValueOrDefault(item.Key)?.Id ?? virtualId--;
            alarm.TagId = plan.DesiredTags[item.TagKey].Id;
            plan.DesiredAlarms[item.Key] = alarm;
        }
        plan.DesiredPanel = ObjectTemplateJson.Copy(plan.Definition.Panel);
        foreach (var node in plan.DesiredPanel.Nodes)
        {
            if (plan.Definition.Bindings.TryGetValue(node.Id, out var key)) node.TagId = plan.DesiredTags[key].Id;
            if (node.Action.Type == "command" && plan.Definition.ActionBindings.TryGetValue(node.Id, out var actionKey)) node.Action.TagId = plan.DesiredTags[actionKey].Id;
        }
    }

    private static async Task ValidatePlanAsync(ConfigDbContext db, Plan plan, CancellationToken ct)
    {
        foreach (var (key, tag) in plan.Tags) { var error = ConfigurationValidator.Tag(tag); if (error is not null) plan.Errors.Add($"{key}：{error}"); }
        foreach (var (key, alarm) in plan.Alarms) { var error = ConfigurationValidator.Alarm(alarm); if (error is not null) plan.Errors.Add($"{key}：{error}"); }
        var allTags = await db.Tags.AsNoTracking().ToListAsync(ct);
        var replacedIds = plan.Tags.Values.Select(x => x.Id).Concat(plan.DeleteTagIds).ToHashSet();
        foreach (var tag in plan.Tags.Values)
            if (allTags.Any(x => !replacedIds.Contains(x.Id) && x.DeviceId == tag.DeviceId && x.Name == tag.Name)) plan.Errors.Add($"测点名称冲突：{tag.Name}");
        if (plan.Tags.Values.GroupBy(x => (x.DeviceId, x.Name)).Any(x => x.Count() > 1)) plan.Errors.Add("合并后出现同名测点");
        var tagMap = allTags.Where(x => !plan.DeleteTagIds.Contains(x.Id)).ToDictionary(x => x.Id);
        foreach (var tag in plan.Tags.Values) tagMap[tag.Id] = tag;
        var targets = (await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        if (plan.Screen is not null) targets.Add(plan.Screen.Id);
        plan.Errors.AddRange(ScreenValidator.Validate(plan.Panel, tagMap, targets).Select(x => $"面板 {x.NodeId}：{x.Message}"));
        var retainedAlarms = await db.AlarmRules.AsNoTracking().Where(x => !plan.DeleteAlarmIds.Contains(x.Id)).ToListAsync(ct);
        foreach (var alarm in plan.Alarms.Values) { retainedAlarms.RemoveAll(x => x.Id == alarm.Id); retainedAlarms.Add(alarm); }
        if (retainedAlarms.Any(x => plan.DeleteTagIds.Contains(x.TagId))) plan.Errors.Add("拟删除的测点仍被保留报警引用，请保留此测点或先处理报警");
        if (plan.DeleteTagIds.Count > 0)
        {
            var screens = await db.ProcessScreens.AsNoTracking().ToListAsync(ct);
            foreach (var screen in screens)
            {
                var draft = screen.Id == plan.Screen?.Id ? plan.Panel : ObjectTemplateJson.Read<ScreenDocument>(screen.DraftJson);
                var published = screen.PublishedJson is null ? null : ObjectTemplateJson.Read<ScreenDocument>(screen.PublishedJson);
                if (References(draft, plan.DeleteTagIds) || published is not null && References(published, plan.DeleteTagIds))
                    plan.Errors.Add($"面板“{screen.Name}”的草稿或运行版仍引用待删除测点，请先发布新面板或选择保留测点");
            }
        }
    }

    private static bool References(ScreenDocument doc, List<int> ids) => doc.Nodes.Any(n => n.TagId is int id && ids.Contains(id) || n.Action.Type == "command" && n.Action.TagId is int output && ids.Contains(output));
    private static List<string> BatchErrors(List<Plan> plans)
    {
        var errors = new List<string>();
        if (plans.GroupBy(x => (x.Parameters.DeviceId, x.Parameters.Name)).Any(x => x.Count() > 1)) errors.Add("批次中对象名称重复");
        if (plans.SelectMany(x => x.Tags.Values).GroupBy(x => (x.DeviceId, x.Name)).Any(x => x.Count() > 1)) errors.Add("批次合并后测点名称重复");
        return errors;
    }
    private static void RequireValid(List<Plan> plans)
    {
        var preview = new ObjectBatchPreview(plans.Select(x => x.Preview()).ToList(), BatchErrors(plans));
        if (!preview.CanApply) throw new TemplateOperationException("预览存在冲突或错误，未修改现场配置", 400, preview);
    }

    private static async Task<object> ApplyAsync(ConfigDbContext db, Plan plan, string userName, CancellationToken ct)
    {
        var now = ScadaTime.NowMs();
        if (plan.DeleteAlarmIds.Count > 0 || plan.DeleteTagIds.Count > 0)
        {
            var obsoleteRules = await db.AlarmRules.Where(x => plan.DeleteAlarmIds.Contains(x.Id) || plan.DeleteTagIds.Contains(x.TagId)).Select(x => x.Id).ToListAsync(ct);
            await db.AlarmEvents.Where(x => x.EndTs == null && x.RuleId != null && obsoleteRules.Contains(x.RuleId.Value)).ExecuteUpdateAsync(x => x.SetProperty(a => a.EndTs, now), ct);
            await db.AlarmRules.Where(x => plan.DeleteAlarmIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
        }
        var actualIds = new Dictionary<int, int>();
        var renamedIds = plan.Tags.Values.Where(t => plan.ExistingTags.TryGetValue(t.Id, out var old) && (old.Name != t.Name || old.DeviceId != t.DeviceId))
            .Select(x => x.Id).Concat(plan.DeleteTagIds).Distinct().ToList();
        if (renamedIds.Count > 0)
        {
            // Free unique names inside this transaction before applying simultaneous renames or key replacements.
            var prefix = "__template_" + Guid.NewGuid().ToString("N") + "_";
            await db.Tags.Where(x => renamedIds.Contains(x.Id)).ExecuteUpdateAsync(x => x.SetProperty(t => t.Name, t => prefix + t.Id), ct);
        }
        var tagEntities = new List<(string Key, int SourceId, Tag Entity)>();
        foreach (var (key, item) in plan.Tags)
        {
            var entity = ObjectTemplateJson.Copy(item);
            var exists = plan.ExistingTags.ContainsKey(entity.Id);
            if (exists) db.Tags.Update(entity); else { entity.Id = 0; db.Tags.Add(entity); }
            tagEntities.Add((key, item.Id, entity));
        }
        await db.SaveChangesAsync(ct);
        foreach (var (key, sourceId, entity) in tagEntities) { actualIds[sourceId] = entity.Id; plan.DesiredTags[key].Id = entity.Id; }
        var alarmEntities = new List<(string Key, AlarmRule Entity)>();
        foreach (var (key, item) in plan.Alarms)
        {
            var entity = ObjectTemplateJson.Copy(item);
            entity.TagId = actualIds.GetValueOrDefault(entity.TagId, entity.TagId);
            var exists = plan.ExistingAlarmIds.Contains(entity.Id);
            if (exists) db.AlarmRules.Update(entity); else { entity.Id = 0; db.AlarmRules.Add(entity); }
            alarmEntities.Add((key, entity));
        }
        await db.SaveChangesAsync(ct);
        foreach (var (key, entity) in alarmEntities)
        {
            plan.DesiredAlarms[key].Id = entity.Id;
            plan.DesiredAlarms[key].TagId = actualIds.GetValueOrDefault(plan.DesiredAlarms[key].TagId, plan.DesiredAlarms[key].TagId);
        }
        void Remap(ScreenDocument doc) { foreach (var n in doc.Nodes) { if (n.TagId is int tag) n.TagId = actualIds.GetValueOrDefault(tag, tag); if (n.Action.TagId is int output) n.Action.TagId = actualIds.GetValueOrDefault(output, output); } }
        Remap(plan.Panel); Remap(plan.DesiredPanel);
        var screen = plan.Screen;
        if (screen is null)
        {
            screen = new ProcessScreen { Name = plan.PanelName, DraftJson = ObjectTemplateJson.Write(plan.Panel), UpdatedTs = now, UpdatedBy = userName };
            db.ProcessScreens.Add(screen);
        }
        else if (screen.Name != plan.PanelName || !ObjectTemplateJson.Equal(ObjectTemplateJson.Read<ScreenDocument>(screen.DraftJson), plan.Panel))
        {
            db.ProcessScreens.Attach(screen);
            screen.Name = plan.PanelName; screen.DraftJson = ObjectTemplateJson.Write(plan.Panel); screen.Revision++; screen.UpdatedTs = now; screen.UpdatedBy = userName;
        }
        await db.SaveChangesAsync(ct);
        await db.Tags.Where(x => plan.DeleteTagIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
        var instance = plan.Instance ?? new DeviceObjectInstance { TemplateId = plan.TemplateId, Name = plan.Parameters.Name, DeviceId = plan.Parameters.DeviceId, BaseAddress = plan.Parameters.BaseAddress, ScreenId = screen.Id };
        if (plan.Instance is not null) db.ObjectInstances.Attach(instance);
        instance.AppliedVersion = plan.Version;
        if (plan.Instance is not null) instance.Revision++;
        instance.UpdatedTs = now; instance.UpdatedBy = userName;
        instance.BaselineJson = ObjectTemplateJson.Write(new ObjectInstanceBaseline { Tags = plan.DesiredTags, Alarms = plan.DesiredAlarms, Panel = plan.DesiredPanel, ScreenName = plan.DesiredPanelName });
        if (plan.Instance is null) db.ObjectInstances.Add(instance);
        await db.SaveChangesAsync(ct);
        return new { instance.Id, instance.Name, instance.ScreenId, instance.AppliedVersion, instance.Revision };
    }

    private static void Audit(ConfigDbContext db, int? id, string name, string action) => db.OperationLogs.Add(new OperationLog { UserId = id, UserName = name, Action = action, Ts = ScadaTime.NowMs(), Result = "成功" });

    private sealed class Plan(int templateId, int version, ObjectInstanceParameters parameters, ObjectTemplateDocument definition)
    {
        public int TemplateId = templateId; public int Version = version; public ObjectInstanceParameters Parameters = parameters; public ObjectTemplateDocument Definition = definition;
        public DeviceObjectInstance? Instance; public ProcessScreen? Screen;
        public Dictionary<string, Tag> DesiredTags = []; public Dictionary<string, Tag> Tags = [];
        public Dictionary<string, AlarmRule> DesiredAlarms = []; public Dictionary<string, AlarmRule> Alarms = [];
        public ScreenDocument DesiredPanel = new(); public ScreenDocument Panel = new();
        public string DesiredPanelName => Parameters.Name + " · 设备面板";
        private string? _panelName; public string PanelName { get => _panelName ?? DesiredPanelName; set => _panelName = value; }
        public Dictionary<string, string> Resolutions = []; public List<int> DeleteTagIds = []; public List<int> DeleteAlarmIds = [];
        public Dictionary<int, Tag> ExistingTags = []; public HashSet<int> ExistingAlarmIds = [];
        public List<TemplateConflict> Conflicts = []; public List<TemplateChange> Changes = []; public List<string> Errors = []; public string State = string.Empty;
        public ObjectPlanPreview Preview() => new(Instance?.Id, Parameters.Name, Version, Instance?.Revision ?? 0, State, Tags.Count, Alarms.Count, Panel.Nodes.Count, Changes, Conflicts, Errors);
    }
}
