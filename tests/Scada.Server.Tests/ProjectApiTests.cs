using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Data;

namespace Scada.Server.Tests;

// 工程管理：导出→导入新环境（编号重映射）、冲突处理、全部回滚、区域授权、快照比较与回滚。
public sealed class ProjectApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public ProjectApiTests(AuthorizationTests.Site site) => _site = site;
    private IDbContextFactory<ConfigDbContext> Factory(AuthorizationTests.Site? site = null) => (site ?? _site).Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static string U() => Guid.NewGuid().ToString("N")[..5];

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task<JsonObject> ExportAsync(HttpClient c) => JsonNode.Parse(await (await c.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();
    private static async Task<JsonElement> PreviewAsync(HttpClient c, JsonObject package, string resolution = "Skip", object? resolutions = null) =>
        await Ok(await c.PostAsJsonAsync("/api/project/import/preview", new { package, resolution, resolutions }));
    private static JsonObject Item(JsonObject package, string section, string key) => package["sections"]![section]!.AsArray().OfType<JsonObject>().Single(x => x["key"]!.GetValue<string>() == key);

    // 在环境里建一套互相引用的配置：设备/测点 → 计算测点 → 计算任务（派生测点）→ 报警规则、报表模板、画面。
    private async Task<string> SeedAsync(HttpClient admin)
    {
        var u = U();
        var device = (await Ok(await admin.PostAsJsonAsync("/api/devices", AuthorizationTests.Site.DeviceRequest("泵站" + u, _site.AreaA)))).GetProperty("id").GetInt32();
        var flow = (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(device, "流量")))).GetProperty("id").GetInt32();
        var calc = (await Ok(await admin.PostAsJsonAsync("/api/calculated-tags", new { name = "双倍流量" + u, expression = $"#{flow} * 2", areaId = _site.AreaA }))).GetProperty("id").GetInt32();
        var job = await Ok(await admin.PostAsJsonAsync("/api/calc-jobs", new { name = "累计" + u, kind = "Accumulator", areaId = _site.AreaA, inputTagId = calc, mode = "Flow", factor = 1, resetPeriod = "Day", unit = "m³" }));
        await Ok(await admin.PostAsJsonAsync("/api/alarm-rules", new { tagId = calc, type = "High", threshold = 50, level = "Warning", delaySec = 0, recoverDeadband = 0, enabled = true, message = "高" + u }));
        await Ok(await admin.PostAsJsonAsync("/api/report-templates", new
        {
            name = "日报" + u, period = "Day", areaId = _site.AreaA,
            columns = new object[] { new { key = "q", title = "流量", kind = "TagAvg", tagId = calc, decimals = 1 }, new { key = "s", title = "累计", kind = "CalcSnapshot", jobId = job.GetProperty("id").GetInt32(), output = "period", decimals = 1 } }
        }));
        var screen = (await Ok(await admin.PostAsJsonAsync("/api/screens", new { name = "画面" + u }))).GetProperty("id").GetInt32();
        var document = new { schemaVersion = 1, width = 800, height = 600, background = "#101b2d", nodes = new object[] { new { id = "n1", type = "value", x = 10, y = 10, width = 100, height = 40, tagId = calc } } };
        await Ok(await admin.PutAsJsonAsync($"/api/screens/{screen}", new { name = "画面" + u, document, revision = 1 }));
        var published = await Ok(await admin.PostAsJsonAsync($"/api/screens/{screen}/publish", new { revision = 2 }));
        Assert.True(published.GetProperty("versionId").GetInt32() > 0); // 发布自动快照
        return u;
    }

    [Fact]
    public async Task ExportImportsIntoFreshEnvironmentWithRemappedReferences()
    {
        var admin = await _site.ClientAsync("admin");
        var u = await SeedAsync(admin);
        var package = await ExportAsync(admin);
        Assert.False(package["partial"]!.GetValue<bool>());
        Assert.Equal($"#{{泵站{u}/流量}} * 2", Item(package, "tags", $"@双倍流量{u}")["data"]!["expression"]!.GetValue<string>());

        var target = new AuthorizationTests.Site(); await target.InitializeAsync();
        try
        {
            var other = await target.ClientAsync("admin");
            // 让新环境的编号与源环境错开。
            await Ok(await other.PostAsJsonAsync("/api/devices", AuthorizationTests.Site.DeviceRequest("占位设备", target.AreaA)));
            var preview = await PreviewAsync(other, package);
            Assert.Empty(preview.GetProperty("errors").EnumerateArray());
            Assert.Contains(preview.GetProperty("entries").EnumerateArray(), x => x.GetProperty("section").GetString() == "screens" && x.GetProperty("action").GetString() == "add");
            await using (var db = await Factory(target).CreateDbContextAsync()) Assert.False(await db.Devices.AnyAsync(x => x.Name == "泵站" + u)); // 预览不落库
            var applied = await Ok(await other.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() }));
            Assert.True(applied.GetProperty("versionId").GetInt32() > 0);

            await using var check = await Factory(target).CreateDbContextAsync();
            var device = await check.Devices.SingleAsync(x => x.Name == "泵站" + u);
            var flow = await check.Tags.SingleAsync(x => x.DeviceId == device.Id && x.Name == "流量");
            var calc = await check.Tags.SingleAsync(x => x.Name == "双倍流量" + u);
            Assert.Equal($"#{flow.Id} * 2", calc.Expression);
            Assert.True(await check.TagDependencies.AnyAsync(x => x.TagId == calc.Id && x.InputTagId == flow.Id));
            var job = await check.CalcJobs.SingleAsync(x => x.Name == "累计" + u);
            Assert.Equal(calc.Id, job.InputTagId);
            Assert.True(await check.Tags.AnyAsync(x => x.JobId == job.Id && x.Unit == "m³"));
            Assert.True(await check.AlarmRules.AnyAsync(x => x.TagId == calc.Id && x.Threshold == 50));
            var report = await check.ReportTemplates.SingleAsync(x => x.Name == "日报" + u);
            Assert.True(report.ColumnsJson.Contains($"\"tagId\":{calc.Id}"), report.ColumnsJson); Assert.Contains($"\"jobId\":{job.Id}", report.ColumnsJson);
            var screen = await check.ProcessScreens.SingleAsync(x => x.Name == "画面" + u);
            Assert.Contains($"\"tagId\":{calc.Id}", screen.PublishedJson);
            // 再次预览：全部不变。
            var again = await PreviewAsync(other, package);
            Assert.Empty(again.GetProperty("entries").EnumerateArray());
            var log = await target.LastLogAsync(x => x.Action == "导入工程并发布");
            Assert.Contains("泵站" + u, log!.Details);
        }
        finally { await target.DisposeAsync(); }
    }

    [Fact]
    public async Task ConflictsCanOverwriteSkipOrRenameAndFailuresRollBackEverything()
    {
        var admin = await _site.ClientAsync("admin");
        var u = await SeedAsync(admin);
        var package = await ExportAsync(admin);
        var device = Item(package, "devices", "泵站" + u)["data"]!.AsObject(); device["pollIntervalMs"] = 4321;
        var report = Item(package, "reportTemplates", "日报" + u)["data"]!.AsObject(); report["scheduleDelayMin"] = 33;
        var rule = package["sections"]!["alarmRules"]!.AsArray().OfType<JsonObject>().Single(x => x["data"]!["message"]?.GetValue<string>() == "高" + u)["data"]!.AsObject(); rule["threshold"] = 77;
        var resolutions = new Dictionary<string, string> { [$"devices:泵站{u}"] = "Overwrite", [$"reportTemplates:日报{u}"] = "Rename" };
        var preview = await PreviewAsync(admin, package, "Skip", resolutions);
        var actions = preview.GetProperty("entries").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("action").GetString());
        Assert.Equal("update", actions["泵站" + u]); Assert.Equal("rename", actions["日报" + u]); Assert.Equal("skip", actions.Single(x => x.Key.StartsWith($"@双倍流量{u}|High")).Value);
        var change = preview.GetProperty("entries").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "泵站" + u).GetProperty("changes")[0];
        Assert.Equal("pollIntervalMs", change.GetProperty("field").GetString()); Assert.Equal("4321", change.GetProperty("new").GetString());
        await Ok(await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", resolutions, baseSha = preview.GetProperty("baseSha").GetString() }));
        await using (var db = await Factory().CreateDbContextAsync())
        {
            Assert.Equal(4321, (await db.Devices.SingleAsync(x => x.Name == "泵站" + u)).PollIntervalMs);
            Assert.Equal(10, (await db.ReportTemplates.SingleAsync(x => x.Name == "日报" + u)).ScheduleDelayMin);
            Assert.Equal(33, (await db.ReportTemplates.SingleAsync(x => x.Name == $"日报{u}_导入")).ScheduleDelayMin);
            Assert.Equal(50, (await db.AlarmRules.SingleAsync(x => x.Message == "高" + u)).Threshold);
        }

        // 一项坏数据导致整体失败：新增设备也不能留下。
        var bad = await ExportAsync(admin);
        var devices = bad["sections"]!["devices"]!.AsArray();
        var extra = (JsonObject)Item(bad, "devices", "泵站" + u).DeepClone(); extra["key"] = "新设备" + u; extra["data"]!["name"] = "新设备" + u; devices.Add(extra);
        var badRule = (JsonObject)bad["sections"]!["alarmRules"]![0]!.DeepClone(); badRule["key"] = "x|High|9"; badRule["data"]!["tagRef"] = "不存在/测点"; bad["sections"]!["alarmRules"]!.AsArray().Add(badRule);
        var badPreview = await PreviewAsync(admin, bad);
        Assert.Contains(badPreview.GetProperty("errors").EnumerateArray(), x => x.GetString()!.Contains("不存在/测点"));
        var failed = await admin.PostAsJsonAsync("/api/project/import", new { package = bad, resolution = "Skip", baseSha = badPreview.GetProperty("baseSha").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        await using (var db = await Factory().CreateDbContextAsync()) Assert.False(await db.Devices.AnyAsync(x => x.Name == "新设备" + u));
        // 预览后配置被改动：拒绝应用。
        var stale = await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Overwrite", baseSha = "0000" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task ImportAndExportFollowAreaPermissions()
    {
        var admin = await _site.ClientAsync("admin"); var cfg = await _site.ClientAsync("cfgA"); var op = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/project/export")).StatusCode);
        var partial = await ExportAsync(cfg);
        Assert.True(partial["partial"]!.GetValue<bool>());
        Assert.DoesNotContain(partial["sections"]!["devices"]!.AsArray(), x => x!["key"]!.GetValue<string>() == "B区设备");
        Assert.Contains(partial["sections"]!["devices"]!.AsArray(), x => x!["key"]!.GetValue<string>() == "A区设备");
        Assert.Empty(partial["sections"]!["screens"]!.AsArray()); // 画面需全局配置权限

        var full = await ExportAsync(admin);
        Item(full, "devices", "B区设备")["data"]!["pollIntervalMs"] = 2222;
        var preview = await PreviewAsync(cfg, full, "Overwrite");
        Assert.Contains(preview.GetProperty("forbidden").EnumerateArray(), x => x.GetString()!.Contains("B区设备"));
        var denied = await cfg.PostAsJsonAsync("/api/project/import", new { package = full, resolution = "Overwrite", baseSha = preview.GetProperty("baseSha").GetString() });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.StartsWith("拒绝", (await _site.LastLogAsync(x => x.UserName == "cfgA" && x.Action == "导入工程"))!.Result);
        await using var db = await Factory().CreateDbContextAsync();
        Assert.NotEqual(2222, (await db.Devices.SingleAsync(x => x.Name == "B区设备")).PollIntervalMs);
    }

    [Fact]
    public async Task SnapshotsCompareAndRollbackWithImpactPreview()
    {
        var admin = await _site.ClientAsync("admin");
        var u = U();
        var snapshot = await Ok(await admin.PostAsJsonAsync("/api/project/versions", new { note = "基线" + u }));
        var v1 = snapshot.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/project/versions", new { note = "" })).StatusCode);
        var device = (await Ok(await admin.PostAsJsonAsync("/api/devices", AuthorizationTests.Site.DeviceRequest("临时" + u, _site.AreaA)))).GetProperty("id").GetInt32();
        await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(device, "临时点")));
        var seeded = await SeedAsync(admin); // 计算测点、计算任务（派生测点）、报警规则、报表模板、已发布画面
        await using (var db = await Factory().CreateDbContextAsync())
            await db.Devices.Where(x => x.Id == _site.DeviceA).ExecuteUpdateAsync(x => x.SetProperty(d => d.PollIntervalMs, 3333));

        var diff = await Ok(await admin.GetAsync($"/api/project/diff?from={v1}&to=current"));
        var entries = diff.GetProperty("entries").EnumerateArray().ToList();
        Assert.Contains(entries, x => x.GetProperty("key").GetString() == "临时" + u && x.GetProperty("action").GetString() == "add");
        Assert.Contains(entries, x => x.GetProperty("key").GetString() == "A区设备" && x.GetProperty("action").GetString() == "update");

        var cfg = await _site.ClientAsync("cfgA");
        var cfgDiff = await Ok(await cfg.GetAsync($"/api/project/diff?from={v1}&to=current"));
        Assert.True(cfgDiff.GetProperty("hidden").GetInt32() > 0); // B 区与全局对象对 cfgA 隐藏

        var impact = await Ok(await admin.PostAsync($"/api/project/versions/{v1}/rollback/preview", null));
        var actions = impact.GetProperty("entries").EnumerateArray().ToDictionary(x => x.GetProperty("finalKey").GetString()!, x => x.GetProperty("action").GetString());
        Assert.Equal("delete", actions["临时" + u]); Assert.Equal("delete", actions[$"临时{u}/临时点"]); Assert.Equal("update", actions["A区设备"]);
        Assert.Empty(impact.GetProperty("errors").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/project/versions/{v1}/rollback", new { baseSha = "stale" })).StatusCode);
        var rolled = await Ok(await admin.PostAsJsonAsync($"/api/project/versions/{v1}/rollback", new { baseSha = impact.GetProperty("baseSha").GetString() }));

        await using (var db = await Factory().CreateDbContextAsync())
        {
            Assert.False(await db.Devices.AnyAsync(x => x.Name == "临时" + u));
            Assert.False(await db.Tags.AnyAsync(x => x.Name.Contains(seeded)));
            Assert.False(await db.CalcJobs.AnyAsync(x => x.Name.Contains(seeded)));
            Assert.False(await db.ProcessScreens.AnyAsync(x => x.Name.Contains(seeded)));
            Assert.False(await db.ReportTemplates.AnyAsync(x => x.Name.Contains(seeded)));
            Assert.Equal(1000, (await db.Devices.SingleAsync(x => x.Id == _site.DeviceA)).PollIntervalMs); // 编号保留，只回写内容
            var versions = await db.ConfigVersions.AsNoTracking().OrderByDescending(x => x.Id).Take(2).ToListAsync();
            Assert.Equal("rollback", versions[0].Kind); Assert.Equal("pre-rollback", versions[1].Kind);
            Assert.Equal(snapshot.GetProperty("contentSha256").GetString(), versions[0].ContentSha256);
        }
        var after = await Ok(await admin.GetAsync($"/api/project/diff?from={v1}&to=current"));
        Assert.Empty(after.GetProperty("entries").EnumerateArray());
        Assert.Equal(rolled.GetProperty("versionId").GetInt32(), (await admin.GetFromJsonAsync<JsonElement[]>("/api/project/versions"))![0].GetProperty("id").GetInt32());
        var log = await _site.LastLogAsync(x => x.Action == $"回滚到配置版本 #{v1}");
        Assert.Contains("pollIntervalMs", log!.Details);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.GetAsync($"/api/project/versions/{v1}/download")).StatusCode);
    }
}
