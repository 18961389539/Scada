using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Templates;

namespace Scada.Engine.Tests;

public class ObjectTemplateTests
{
    [Fact]
    public void PumpDefinitionIsValidAndUsesSymbolicBindings()
    {
        var doc = ObjectTemplateExamples.Pump();
        Assert.Empty(ObjectTemplateValidator.Validate(doc));
        Assert.All(doc.Panel.Nodes, n => { Assert.Null(n.TagId); Assert.Null(n.Action.TagId); });
        doc.Bindings["pump"] = "missing";
        Assert.Contains(ObjectTemplateValidator.Validate(doc), x => x.Contains("输入逻辑键不存在"));
    }

    [Fact]
    public void ValidatorRejectsDuplicateKeysAndUnsafeCommandValues()
    {
        var doc = ObjectTemplateExamples.Pump(); doc.Tags[1].Key = "run";
        Assert.Contains(ObjectTemplateValidator.Validate(doc), x => x.Contains("重复"));
        doc = ObjectTemplateExamples.Pump(); doc.Panel.Nodes.Single(x => x.Id == "start").Action.Value = .5;
        Assert.Contains(ObjectTemplateValidator.Validate(doc), x => x.Contains("开关量"));
        doc = ObjectTemplateExamples.Pump(); doc.Tags[1].AddressOffset = 65535;
        Assert.Contains(ObjectTemplateValidator.Validate(doc), x => x.Contains("地址"));
    }

    [Fact]
    public async Task BatchCreatesTagsAlarmsAndIndependentDraftPanelsWithResolvedIds()
    {
        await using var rig = await Rig.CreateAsync();
        var request = new ObjectBatchRequest(1, [new("P01", 1, 100), new("P02", 1, 200)]);
        var preview = await rig.Service.PreviewCreateAsync(rig.TemplateId, request, default);
        Assert.True(preview.CanApply, ObjectTemplateJson.Write(preview));
        await rig.Service.CreateAsync(rig.TemplateId, request, null, "test", default);
        await using var db = rig.Factory.CreateDbContext();
        var instances = await db.ObjectInstances.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, instances.Count); Assert.Equal(6, await db.Tags.CountAsync()); Assert.Equal(2, await db.AlarmRules.CountAsync());
        foreach (var instance in instances)
        {
            var baseline = ObjectTemplateJson.Read<ObjectInstanceBaseline>(instance.BaselineJson);
            Assert.Equal(instance.BaseAddress + 2, baseline.Tags["fault"].Address);
            Assert.All(baseline.Tags.Values, t => Assert.StartsWith(instance.Name + ".", t.Name));
            var panel = await db.ProcessScreens.FindAsync(instance.ScreenId); Assert.Null(panel!.PublishedJson);
            var document = ObjectTemplateJson.Read<ScreenDocument>(panel.DraftJson);
            Assert.Equal(baseline.Tags["frequency"].Id, document.Nodes.Single(n => n.Id == "frequency").TagId);
            Assert.Equal(baseline.Tags["run"].Id, document.Nodes.Single(n => n.Id == "start").Action.TagId);
            Assert.Equal(baseline.Tags["fault"].Id, baseline.Alarms["fault"].TagId);
        }
    }

    [Fact]
    public async Task InvalidBatchDoesNotPartiallyCreateValidObjects()
    {
        await using var rig = await Rig.CreateAsync();
        var request = new ObjectBatchRequest(1, [new("valid", 1, 0), new("overflow", 1, 65535)]);
        var preview = await rig.Service.PreviewCreateAsync(rig.TemplateId, request, default);
        Assert.False(preview.CanApply);
        await Assert.ThrowsAsync<TemplateOperationException>(() => rig.Service.CreateAsync(rig.TemplateId, request, null, "test", default));
        await using var db = rig.Factory.CreateDbContext();
        Assert.Empty(await db.ObjectInstances.ToListAsync()); Assert.Empty(await db.Tags.ToListAsync()); Assert.Empty(await db.ProcessScreens.ToListAsync());
        var duplicate = new ObjectBatchRequest(1, [new("same", 1, 0), new("same", 1, 100)]);
        Assert.False((await rig.Service.PreviewCreateAsync(rig.TemplateId, duplicate, default)).CanApply);
    }

    [Fact]
    public async Task UpgradePreservesLocalOverrideAndAppliesIndependentTemplateChanges()
    {
        await using var rig = await Rig.CreateAsync(); var instance = await rig.CreateInstanceAsync();
        var baseline = ObjectTemplateJson.Read<ObjectInstanceBaseline>(instance.BaselineJson);
        await using (var db = rig.Factory.CreateDbContext())
        {
            var tag = await db.Tags.FindAsync(baseline.Tags["frequency"].Id); tag!.Unit = "rpm"; await db.SaveChangesAsync();
        }
        var updated = ObjectTemplateExamples.Pump(); updated.Tags.Single(t => t.Key == "frequency").Properties.Description = "新版描述";
        await rig.PublishAsync(2, updated);
        var preview = await rig.Service.PreviewUpgradeAsync(instance.Id, new(2, instance.Revision, null, null), default);
        Assert.True(preview.CanApply, ObjectTemplateJson.Write(preview));
        await rig.ApplyAsync(instance, preview);
        await using var result = rig.Factory.CreateDbContext();
        var merged = await result.Tags.FindAsync(baseline.Tags["frequency"].Id);
        Assert.Equal("rpm", merged!.Unit); Assert.Equal("新版描述", merged.Description);
        Assert.Equal(2, (await result.ObjectInstances.FindAsync(instance.Id))!.AppliedVersion);
    }

    [Fact]
    public async Task DivergentFieldsRequireResolutionAndStalePreviewCannotApply()
    {
        await using var rig = await Rig.CreateAsync(); var instance = await rig.CreateInstanceAsync();
        var baseline = ObjectTemplateJson.Read<ObjectInstanceBaseline>(instance.BaselineJson);
        await using (var db = rig.Factory.CreateDbContext()) { var tag = await db.Tags.FindAsync(baseline.Tags["frequency"].Id); tag!.MaxValue = 60; await db.SaveChangesAsync(); }
        var updated = ObjectTemplateExamples.Pump(); updated.Tags.Single(t => t.Key == "frequency").Properties.MaxValue = 80;
        await rig.PublishAsync(2, updated);
        var pending = await rig.Service.PreviewUpgradeAsync(instance.Id, new(2, instance.Revision, null, null), default);
        Assert.False(pending.CanApply); Assert.Contains(pending.Conflicts, x => x.Key == "tag.frequency.MaxValue");
        var resolutions = new Dictionary<string, string> { ["tag.frequency.MaxValue"] = "keepLocal" };
        var ready = await rig.Service.PreviewUpgradeAsync(instance.Id, new(2, instance.Revision, null, resolutions), default);
        Assert.True(ready.CanApply);
        await using (var db = rig.Factory.CreateDbContext()) { var tag = await db.Tags.FindAsync(baseline.Tags["frequency"].Id); tag!.Unit = "changed after preview"; await db.SaveChangesAsync(); }
        var ex = await Assert.ThrowsAsync<TemplateOperationException>(() => rig.ApplyAsync(instance, ready, resolutions)); Assert.Equal(409, ex.Status);
        var refreshed = await rig.Service.PreviewUpgradeAsync(instance.Id, new(2, instance.Revision, null, resolutions), default);
        await rig.ApplyAsync(instance, refreshed, resolutions);
        await using var final = rig.Factory.CreateDbContext(); Assert.Equal(60, (await final.Tags.FindAsync(baseline.Tags["frequency"].Id))!.MaxValue);
    }

    [Fact]
    public async Task MissingMembersAreRegeneratedWithDistinctIdsAndBindings()
    {
        await using var rig = await Rig.CreateAsync(); var instance = await rig.CreateInstanceAsync();
        var before = ObjectTemplateJson.Read<ObjectInstanceBaseline>(instance.BaselineJson);
        await using (var db = rig.Factory.CreateDbContext()) { await db.Tags.Where(x => x.Id == before.Tags["run"].Id || x.Id == before.Tags["frequency"].Id).ExecuteDeleteAsync(); }
        await rig.PublishAsync(2, ObjectTemplateExamples.Pump());
        var resolutions = new Dictionary<string, string> { ["tag.run.$missing"] = "useTemplate", ["tag.frequency.$missing"] = "useTemplate" };
        var plan = await rig.Service.PreviewUpgradeAsync(instance.Id, new(2, instance.Revision, null, resolutions), default);
        Assert.True(plan.CanApply, ObjectTemplateJson.Write(plan)); await rig.ApplyAsync(instance, plan, resolutions);
        await using var result = rig.Factory.CreateDbContext();
        var updated = ObjectTemplateJson.Read<ObjectInstanceBaseline>((await result.ObjectInstances.FindAsync(instance.Id))!.BaselineJson);
        Assert.NotEqual(updated.Tags["run"].Id, updated.Tags["frequency"].Id);
        Assert.Equal(3, await result.Tags.CountAsync());
        var panel = ObjectTemplateJson.Read<ScreenDocument>((await result.ProcessScreens.FindAsync(instance.ScreenId))!.DraftJson);
        Assert.Equal(updated.Tags["run"].Id, panel.Nodes.Single(x => x.Id == "start").Action.TagId);
    }

    [Fact]
    public async Task RemovalProtectsPublishedBindingsAndClosesRemovedActiveAlarm()
    {
        await using var rig = await Rig.CreateAsync(); var instance = await rig.CreateInstanceAsync();
        var before = ObjectTemplateJson.Read<ObjectInstanceBaseline>(instance.BaselineJson);
        await using (var db = rig.Factory.CreateDbContext())
        {
            var panel = await db.ProcessScreens.FindAsync(instance.ScreenId); panel!.PublishedJson = panel.DraftJson; panel.PublishedRevision = panel.Revision; panel.PublishedName = panel.Name;
            db.AlarmEvents.Add(new AlarmEvent { RuleId = before.Alarms["fault"].Id, TagId = before.Tags["fault"].Id, StartTs = 1, Message = "active" }); await db.SaveChangesAsync();
        }
        var updated = ObjectTemplateExamples.Pump(); updated.Tags.RemoveAll(x => x.Key == "fault"); updated.Alarms.Clear(); updated.Bindings.Remove("fault"); updated.Panel.Nodes.RemoveAll(x => x.Id == "fault");
        await rig.PublishAsync(2, updated);
        var resolutions = new Dictionary<string, string> { ["tag.fault.$remove"] = "useTemplate", ["alarm.fault.$remove"] = "useTemplate" };
        var blocked = await rig.Service.PreviewUpgradeAsync(instance.Id, new(2, instance.Revision, null, resolutions), default);
        Assert.False(blocked.CanApply); Assert.Contains(blocked.Errors, e => e.Contains("运行版"));
        resolutions["tag.fault.$remove"] = "keepLocal";
        var ready = await rig.Service.PreviewUpgradeAsync(instance.Id, new(2, instance.Revision, null, resolutions), default);
        Assert.True(ready.CanApply, ObjectTemplateJson.Write(ready)); await rig.ApplyAsync(instance, ready, resolutions);
        await using var result = rig.Factory.CreateDbContext(); Assert.NotNull(await result.Tags.FindAsync(before.Tags["fault"].Id));
        Assert.Empty(await result.AlarmRules.ToListAsync()); Assert.NotNull((await result.AlarmEvents.SingleAsync()).EndTs);
        var screen = await result.ProcessScreens.FindAsync(instance.ScreenId); Assert.NotEqual(screen!.PublishedJson, screen.DraftJson);
    }

    [Fact]
    public async Task UpgradeAddsNewTagsAlarmsAndPanelBindingsUsingAllocatedIds()
    {
        await using var rig = await Rig.CreateAsync(); var instance = await rig.CreateInstanceAsync();
        var updated = ObjectTemplateExamples.Pump();
        updated.Tags.Add(new() { Key = "pressure", AddressOffset = 4, Properties = new Tag { Name = "压力", DeviceId = 1, DataType = TagDataType.Float, Area = TagArea.HoldingRegister } });
        updated.Alarms.Add(new() { Key = "pressureHigh", TagKey = "pressure", Properties = new AlarmRule { TagId = 1, Type = AlarmRuleType.High, Threshold = 5 } });
        updated.Panel.Nodes.Add(new() { Id = "pressureValue", Type = "value", X = 500, Y = 380, Width = 200, Height = 40 }); updated.Bindings["pressureValue"] = "pressure";
        await rig.PublishAsync(2, updated);
        var plan = await rig.Service.PreviewUpgradeAsync(instance.Id, new(2, instance.Revision, null, null), default);
        Assert.True(plan.CanApply, ObjectTemplateJson.Write(plan)); await rig.ApplyAsync(instance, plan);
        await using var db = rig.Factory.CreateDbContext();
        var baseline = ObjectTemplateJson.Read<ObjectInstanceBaseline>((await db.ObjectInstances.FindAsync(instance.Id))!.BaselineJson);
        var tag = await db.Tags.FindAsync(baseline.Tags["pressure"].Id);
        Assert.NotNull(tag); Assert.Equal(tag.Id, (await db.AlarmRules.FindAsync(baseline.Alarms["pressureHigh"].Id))!.TagId);
        var panel = ObjectTemplateJson.Read<ScreenDocument>((await db.ProcessScreens.FindAsync(instance.ScreenId))!.DraftJson);
        Assert.Equal(tag.Id, panel.Nodes.Single(x => x.Id == "pressureValue").TagId);
    }

    [Fact]
    public async Task UpgradeCanSwapMemberNamesWithoutUniqueConstraintFailure()
    {
        await using var rig = await Rig.CreateAsync(); var instance = await rig.CreateInstanceAsync();
        var updated = ObjectTemplateExamples.Pump();
        (updated.Tags[1].Properties.Name, updated.Tags[2].Properties.Name) = (updated.Tags[2].Properties.Name, updated.Tags[1].Properties.Name);
        await rig.PublishAsync(2, updated);
        var preview = await rig.Service.PreviewUpgradeAsync(instance.Id, new(2, 1, null, null), default);
        Assert.True(preview.CanApply, ObjectTemplateJson.Write(preview)); await rig.ApplyAsync(instance, preview);
        await using var db = rig.Factory.CreateDbContext();
        var baseline = ObjectTemplateJson.Read<ObjectInstanceBaseline>((await db.ObjectInstances.FindAsync(instance.Id))!.BaselineJson);
        Assert.Equal("P01.故障代码", (await db.Tags.FindAsync(baseline.Tags["frequency"].Id))!.Name);
        Assert.Equal("P01.频率", (await db.Tags.FindAsync(baseline.Tags["fault"].Id))!.Name);
    }

    [Fact]
    public async Task MaximumObjectBatchCreatesOneHundredIndependentlyBoundPanels()
    {
        await using var rig = await Rig.CreateAsync();
        var parameters = Enumerable.Range(0, 100).Select(i => new ObjectInstanceParameters($"P{i:000}", 1, i * 10)).ToList();
        await rig.Service.CreateAsync(rig.TemplateId, new(1, parameters), null, "test", default);
        await using var db = rig.Factory.CreateDbContext();
        Assert.Equal(100, await db.ObjectInstances.CountAsync()); Assert.Equal(300, await db.Tags.CountAsync());
        var instances = await db.ObjectInstances.ToListAsync();
        Assert.Equal(100, instances.Select(i => ObjectTemplateJson.Read<ObjectInstanceBaseline>(i.BaselineJson).Tags["run"].Id).Distinct().Count());
    }

    [Fact]
    public async Task BatchUpgradeWithOneConflictLeavesAllInstancesUnchanged()
    {
        await using var rig = await Rig.CreateAsync(); var a = await rig.CreateInstanceAsync("A"); var b = await rig.CreateInstanceAsync("B");
        var baseline = ObjectTemplateJson.Read<ObjectInstanceBaseline>(b.BaselineJson);
        await using (var db = rig.Factory.CreateDbContext()) { var tag = await db.Tags.FindAsync(baseline.Tags["frequency"].Id); tag!.MaxValue = 60; await db.SaveChangesAsync(); }
        var updated = ObjectTemplateExamples.Pump(); updated.Tags.Single(x => x.Key == "frequency").Properties.MaxValue = 80;
        await rig.PublishAsync(2, updated);
        var pa = await rig.Service.PreviewUpgradeAsync(a.Id, new(2, 1, null, null), default); var pb = await rig.Service.PreviewUpgradeAsync(b.Id, new(2, 1, null, null), default);
        await Assert.ThrowsAsync<TemplateOperationException>(() => rig.Service.UpgradeAsync(new([new(a.Id, 2, 1, pa.State, []), new(b.Id, 2, 1, pb.State, [])]), null, "test", default));
        await using var result = rig.Factory.CreateDbContext(); Assert.All(await result.ObjectInstances.ToListAsync(), i => Assert.Equal(1, i.AppliedVersion));
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "scada-objects-" + Guid.NewGuid().ToString("N"));
        public Factory Factory { get; }
        public ObjectTemplateService Service { get; }
        public int TemplateId { get; private set; }
        private Rig() { Directory.CreateDirectory(_root); Factory = new Factory(Path.Combine(_root, "test.db")); Service = new ObjectTemplateService(Factory); }
        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig(); await using var db = rig.Factory.CreateDbContext(); await db.Database.EnsureCreatedAsync();
            db.Devices.Add(new Device { Id = 1, Name = "PLC", Enabled = false });
            var template = new DeviceObjectTemplate { Name = "Pump", DraftJson = ObjectTemplateJson.Write(ObjectTemplateExamples.Pump()), LatestVersion = 1, UpdatedBy = "test" };
            db.ObjectTemplates.Add(template); await db.SaveChangesAsync(); rig.TemplateId = template.Id;
            db.ObjectTemplateVersions.Add(new DeviceObjectTemplateVersion { TemplateId = template.Id, Version = 1, DocumentJson = template.DraftJson, CreatedBy = "test" }); await db.SaveChangesAsync(); return rig;
        }
        public async Task<DeviceObjectInstance> CreateInstanceAsync(string name = "P01") { await Service.CreateAsync(TemplateId, new(1, [new(name, 1, 0)]), null, "test", default); await using var db = Factory.CreateDbContext(); return await db.ObjectInstances.AsNoTracking().SingleAsync(x => x.Name == name); }
        public async Task PublishAsync(int version, ObjectTemplateDocument doc) { Assert.Empty(ObjectTemplateValidator.Validate(doc)); await using var db = Factory.CreateDbContext(); db.ObjectTemplateVersions.Add(new DeviceObjectTemplateVersion { TemplateId = TemplateId, Version = version, DocumentJson = ObjectTemplateJson.Write(doc), CreatedBy = "test" }); await db.SaveChangesAsync(); }
        public Task<IReadOnlyList<object>> ApplyAsync(DeviceObjectInstance i, ObjectPlanPreview plan, Dictionary<string, string>? resolutions = null) => Service.UpgradeAsync(new([new(i.Id, plan.Version, i.Revision, plan.State, resolutions ?? [])]), null, "test", default);
        public ValueTask DisposeAsync() { var target = Path.GetFullPath(_root); if (target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(target).StartsWith("scada-objects-", StringComparison.Ordinal)) Directory.Delete(target, true); return ValueTask.CompletedTask; }
    }
    public sealed class Factory(string path) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
