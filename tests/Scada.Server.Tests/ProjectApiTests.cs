using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Models;
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
    public async Task FrozenScreenPublicationsSurviveImportAndSnapshotRollbackWithRemappedReferences()
    {
        using var admin = await _site.ClientAsync("admin");
        var u = U();
        var name = "固化画面" + u;
        var destinationName = "目标画面" + u;
        var deviceName = "固化设备" + u;
        var templateKey = "frozenpanel" + u;
        var symbolKey = "frozensymbol" + u;
        var symbol = "user:" + symbolKey;
        var commandId = 0;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string oldSvg = "<svg viewBox=\"0 0 100 100\"><rect width=\"80\" height=\"80\" fill=\"#123456\"/></svg>";
        const string newSvg = "<svg viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"#abcdef\"/></svg>";
        const string latestSvg = "<svg viewBox=\"0 0 100 100\"><path d=\"M0 0 L80 80\" stroke=\"#ffffff\"/></svg>";
        object SymbolBody(string svg, int revision = 0) => new { key = symbolKey, name = symbolKey, category = "工程回归", kind = "symbol", width = 100, height = 100, svg, revision };
        object TemplateBody(double value, string text, int revision = 0) => new
        {
            key = templateKey, name = templateKey, category = "工程回归", kind = "template", width = 640, height = 300,
            placeholders = new[] { "cmd" }, revision,
            document = new ScreenDocument
            {
                Width = 640, Height = 300,
                Nodes = [
                    new() { Id = "symbol", Type = "symbol", Width = 100, Height = 100, Symbol = symbol },
                    new() { Id = "start", Type = "button", X = 120, Width = 100, Height = 40, Text = text,
                        Action = new() { Type = "command", Bind = "cmd", Value = value } },
                    new() { Id = "panel", Type = "equipment-panel", X = 240, Width = 320, Height = 240,
                        Commands = [new() { Key = "start", Label = text, Bind = "cmd", Value = value }] },
                    new() { Id = "history", Type = "history-table", X = 560, Width = 80, Height = 100, TagBinds = ["cmd"] }
                ]
            }
        };
        void AssertPublication(ProcessScreen row, int tagId, int destinationId, double value, string text, string svg)
        {
            var frozen = JsonSerializer.Deserialize<ScreenDocument>(row.PublishedJson!, options)!;
            Assert.Equal(5, frozen.Nodes.Count);
            Assert.Equal([tagId], frozen.Nodes.Single(x => x.Id == "instance--history").Tags!);
            Assert.DoesNotContain(frozen.Nodes, x => x.Type == "template");
            Assert.All(frozen.Nodes, x => { Assert.Null(x.TemplateKey); Assert.Null(x.Bindings); Assert.Null(x.Action.Bind); });
            Assert.Equal(symbol, frozen.Nodes.Single(x => x.Id == "instance--symbol").Symbol);
            Assert.Equal(symbol, Assert.Single(frozen.SymbolAssets!).Key);
            Assert.Equal(svg, Assert.Single(frozen.SymbolAssets!).Value);
            var start = frozen.Nodes.Single(x => x.Id == "instance--start");
            Assert.Equal(text, start.Text);
            Assert.Equal(tagId, start.Action.TagId);
            Assert.Equal(value, start.Action.Value);
            var command = Assert.Single(frozen.Nodes.Single(x => x.Id == "instance--panel").Commands!);
            Assert.Equal(tagId, command.TagId);
            Assert.Equal(value, command.Value);
            Assert.Null(command.Bind);
            Assert.Equal(destinationId, frozen.Nodes.Single(x => x.Id == "next").Action.ScreenId);
            var draft = JsonSerializer.Deserialize<ScreenDocument>(row.DraftJson, options)!;
            Assert.Null(draft.SymbolAssets);
            var instance = draft.Nodes.Single(x => x.Id == "instance");
            Assert.Equal("template", instance.Type);
            Assert.Equal(templateKey, instance.TemplateKey);
            Assert.Equal(tagId, Assert.Single(instance.Bindings!).TagId);
            Assert.Equal(destinationId, draft.Nodes.Single(x => x.Id == "next").Action.ScreenId);
        }
        var deviceId = (await Ok(await admin.PostAsJsonAsync("/api/devices", AuthorizationTests.Site.DeviceRequest(deviceName, _site.AreaA)))).GetProperty("id").GetInt32();
        commandId = (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(deviceId, "控制")))).GetProperty("id").GetInt32();
        var destinationId = (await Ok(await admin.PostAsJsonAsync("/api/screens", new { name = destinationName }))).GetProperty("id").GetInt32();
        await Ok(await admin.PostAsJsonAsync($"/api/screens/{destinationId}/publish", new { revision = 1 }));
        var symbolItem = (await Ok(await admin.PostAsJsonAsync("/api/hmi/library", SymbolBody(oldSvg)))).GetProperty("item");
        var templateItem = (await Ok(await admin.PostAsJsonAsync("/api/hmi/library", TemplateBody(11, "旧命令")))).GetProperty("item");
        var screenId = (await Ok(await admin.PostAsJsonAsync("/api/screens", new { name }))).GetProperty("id").GetInt32();
        var document = new ScreenDocument
        {
            Width = 800, Height = 480,
            Nodes = [
                new() { Id = "instance", Type = "template", TemplateKey = templateKey, Width = 640, Height = 300,
                    Bindings = [new() { Key = "cmd", TagId = commandId }] },
                new() { Id = "next", Type = "button", Y = 320, Width = 120, Height = 40,
                    Action = new() { Type = "navigate", ScreenId = destinationId } }
            ]
        };
        await Ok(await admin.PutAsJsonAsync($"/api/screens/{screenId}", new { name, document, revision = 1 }));
        await Ok(await admin.PostAsJsonAsync($"/api/screens/{screenId}/publish", new { revision = 2 }));
        ProcessScreen original;
        await using (var db = await Factory().CreateDbContextAsync())
        {
            original = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == screenId);
            AssertPublication(original, commandId, destinationId, 11, "旧命令", oldSvg);
        }
        await Ok(await admin.PutAsJsonAsync($"/api/hmi/library/{templateItem.GetProperty("id").GetInt32()}", TemplateBody(22, "新图库", templateItem.GetProperty("revision").GetInt32())));
        await Ok(await admin.PutAsJsonAsync($"/api/hmi/library/{symbolItem.GetProperty("id").GetInt32()}", SymbolBody(newSvg, symbolItem.GetProperty("revision").GetInt32())));
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var unchanged = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == screenId);
            Assert.Equal(original.PublishedJson, unchanged.PublishedJson);
            Assert.Equal(original.PublishedRevision, unchanged.PublishedRevision);
        }
        var package = await ExportAsync(admin);
        var exported = Item(package, "screens", name)["data"]!;
        var exportedNodes = exported["published"]!["nodes"]!.AsArray();
        Assert.DoesNotContain(exportedNodes, x => x!["type"]!.GetValue<string>() == "template");
        var exportedStart = exportedNodes.Single(x => x!["id"]!.GetValue<string>() == "instance--start")!;
        Assert.Equal(deviceName + "/控制", exportedStart["action"]!["tagRef"]!.GetValue<string>());
        Assert.Equal(11, exportedStart["action"]!["value"]!.GetValue<double>());
        var exportedPanel = exportedNodes.Single(x => x!["id"]!.GetValue<string>() == "instance--panel")!;
        Assert.Equal(deviceName + "/控制", exportedPanel["commands"]![0]!["tagRef"]!.GetValue<string>());
        var exportedHistory = exportedNodes.Single(x => x!["id"]!.GetValue<string>() == "instance--history")!;
        Assert.Equal(deviceName + "/控制", exportedHistory["tagRefs"]![0]!.GetValue<string>());
        Assert.Null(exportedHistory["tags"]);
        Assert.Equal(destinationName, exportedNodes.Single(x => x!["id"]!.GetValue<string>() == "next")!["action"]!["screenRef"]!.GetValue<string>());
        Assert.Equal(oldSvg, exported["published"]!["symbolAssets"]![symbol]!.GetValue<string>());
        Assert.Equal("template", exported["draft"]!["nodes"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(newSvg, Item(package, "hmiLibrary", symbolKey)["data"]!["svg"]!.GetValue<string>());
        Assert.Equal(22, Item(package, "hmiLibrary", templateKey)["data"]!["document"]!["nodes"]![1]!["action"]!["value"]!.GetValue<double>());

        var target = new AuthorizationTests.Site(); await target.InitializeAsync();
        try
        {
            using var other = await target.ClientAsync("admin");
            await using (var db = await Factory(target).CreateDbContextAsync())
            {
                // 显式占位使编号必然错开，不依赖源环境中其他用例的执行顺序。
                db.Tags.Add(new Tag { Id = commandId + 1000, DeviceId = target.DeviceA, Name = "编号占位" + u, Writable = true });
                db.ProcessScreens.Add(new ProcessScreen { Id = Math.Max(screenId, destinationId) + 1000, Name = "编号占位画面" + u,
                    DraftJson = JsonSerializer.Serialize(new ScreenDocument(), options), UpdatedBy = "seed" });
                await db.SaveChangesAsync();
            }
            var preview = await PreviewAsync(other, package);
            Assert.Empty(preview.GetProperty("errors").EnumerateArray());
            await Ok(await other.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() }));
            ProcessScreen imported;
            int importedCommandId, importedDestinationId;
            HmiLibraryItem importedTemplate, importedSymbol;
            await using (var db = await Factory(target).CreateDbContextAsync())
            {
                var importedDevice = await db.Devices.AsNoTracking().SingleAsync(x => x.Name == deviceName);
                importedCommandId = (await db.Tags.AsNoTracking().SingleAsync(x => x.DeviceId == importedDevice.Id && x.Name == "控制")).Id;
                importedDestinationId = (await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Name == destinationName)).Id;
                imported = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Name == name);
                importedTemplate = await db.HmiLibrary.AsNoTracking().SingleAsync(x => x.Key == templateKey);
                importedSymbol = await db.HmiLibrary.AsNoTracking().SingleAsync(x => x.Key == symbolKey);
                Assert.NotEqual(commandId, importedCommandId);
                Assert.NotEqual(screenId, imported.Id);
                Assert.NotEqual(destinationId, importedDestinationId);
                AssertPublication(imported, importedCommandId, importedDestinationId, 11, "旧命令", oldSvg);
                Assert.Equal(newSvg, importedSymbol.Svg);
                Assert.Equal(22, JsonSerializer.Deserialize<ScreenDocument>(importedTemplate.DocumentJson!, options)!.Nodes.Single(x => x.Id == "start").Action.Value);
            }
            var snapshotId = (await Ok(await other.PostAsJsonAsync("/api/project/versions", new { note = "旧发布与新图库分别固化" + u }))).GetProperty("id").GetInt32();
            await Ok(await other.PutAsJsonAsync($"/api/hmi/library/{importedTemplate.Id}", TemplateBody(33, "再改图库", importedTemplate.Revision)));
            await Ok(await other.PutAsJsonAsync($"/api/hmi/library/{importedSymbol.Id}", SymbolBody(latestSvg, importedSymbol.Revision)));
            var republished = await Ok(await other.PostAsJsonAsync($"/api/screens/{imported.Id}/publish", new { revision = imported.Revision }));
            var republishedRevision = republished.GetProperty("publishedRevision").GetInt32();
            Assert.True(republishedRevision > imported.PublishedRevision);
            await using (var db = await Factory(target).CreateDbContextAsync())
                AssertPublication(await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == imported.Id), importedCommandId, importedDestinationId, 33, "再改图库", latestSvg);
            var rollback = await Ok(await other.PostAsync($"/api/project/versions/{snapshotId}/rollback/preview", null));
            Assert.Empty(rollback.GetProperty("errors").EnumerateArray());
            await Ok(await other.PostAsJsonAsync($"/api/project/versions/{snapshotId}/rollback", new { baseSha = rollback.GetProperty("baseSha").GetString() }));
            await using (var db = await Factory(target).CreateDbContextAsync())
            {
                var restored = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == imported.Id);
                Assert.True(restored.PublishedRevision > republishedRevision);
                Assert.Equal(restored.Revision, restored.PublishedRevision);
                Assert.Equal(imported.PublishedJson, restored.PublishedJson);
                Assert.Equal(imported.DraftJson, restored.DraftJson);
                AssertPublication(restored, importedCommandId, importedDestinationId, 11, "旧命令", oldSvg);
                Assert.Equal(newSvg, (await db.HmiLibrary.AsNoTracking().SingleAsync(x => x.Key == symbolKey)).Svg);
                var restoredTemplate = await db.HmiLibrary.AsNoTracking().SingleAsync(x => x.Key == templateKey);
                Assert.Equal(22, JsonSerializer.Deserialize<ScreenDocument>(restoredTemplate.DocumentJson!, options)!.Nodes.Single(x => x.Id == "start").Action.Value);
            }
            var running = await Ok(await other.GetAsync($"/api/screens/{imported.Id}"));
            Assert.Equal(oldSvg, running.GetProperty("document").GetProperty("symbolAssets").GetProperty(symbol).GetString());
            Assert.Equal(11, running.GetProperty("document").GetProperty("nodes").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "instance--start").GetProperty("action").GetProperty("value").GetDouble());
        }
        finally { await target.DisposeAsync(); }
    }

    [Fact]
    public async Task ProjectPreviewRejectsScriptInPublishedSymbolAssetsWithoutPersistingChanges()
    {
        using var admin = await _site.ClientAsync("admin");
        var u = U();
        var name = "恶意快照画面" + u;
        var symbolKey = "invalidasset" + u;
        var symbol = "user:" + symbolKey;
        var deviceName = "失败不得保留设备" + u;
        var package = await ExportAsync(admin);
        var device = (JsonObject)Item(package, "devices", "A区设备").DeepClone();
        device["key"] = deviceName; device["data"]!["name"] = deviceName;
        package["sections"]!["devices"]!.AsArray().Add(device);
        package["sections"]!["hmiLibrary"]!.AsArray().Add(new JsonObject
        {
            ["key"] = symbolKey,
            ["data"] = new JsonObject { ["key"] = symbolKey, ["name"] = symbolKey, ["category"] = "工程回归", ["kind"] = "symbol",
                ["width"] = 100, ["height"] = 100, ["svg"] = "<svg viewBox=\"0 0 100 100\"><rect width=\"80\" height=\"80\"/></svg>" }
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        package["sections"]!["screens"]!.AsArray().Add(new JsonObject
        {
            ["key"] = name,
            ["data"] = new JsonObject
            {
                ["name"] = name, ["publishedName"] = name,
                ["draft"] = JsonSerializer.SerializeToNode(new ScreenDocument(), options),
                ["published"] = JsonSerializer.SerializeToNode(new ScreenDocument
                {
                    Nodes = [new() { Id = "symbol", Type = "symbol", Symbol = symbol, Width = 100, Height = 100 }],
                    SymbolAssets = new() { [symbol] = "<svg><script>alert(1)</script></svg>" }
                }, options)
            }
        });
        var projects = _site.Services.GetRequiredService<Scada.Server.Projects.ProjectService>();
        async Task<string> PersistedStateAsync()
        {
            await using var db = await Factory().CreateDbContextAsync();
            return JsonSerializer.Serialize(new
            {
                sections = (await projects.ExportAsync(null, "test", CancellationToken.None)).Sections,
                versions = await db.ConfigVersions.CountAsync(),
                screens = await db.ProcessScreens.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
                library = await db.HmiLibrary.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
            });
        }
        var before = await PersistedStateAsync();
        int originalLogs;
        await using (var db = await Factory().CreateDbContextAsync()) originalLogs = await db.OperationLogs.CountAsync();
        var preview = await PreviewAsync(admin, package);
        Assert.Contains(preview.GetProperty("errors").EnumerateArray(), error => error.GetString()!.Contains("符号快照") && error.GetString()!.Contains("script", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, await PersistedStateAsync());
        await using (var db = await Factory().CreateDbContextAsync()) Assert.Equal(originalLogs, await db.OperationLogs.CountAsync());
        var refused = await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(before, await PersistedStateAsync());
        await using var check = await Factory().CreateDbContextAsync();
        Assert.False(await check.Devices.AnyAsync(x => x.Name == deviceName));
        Assert.False(await check.HmiLibrary.AnyAsync(x => x.Key == symbolKey));
        Assert.False(await check.ProcessScreens.AnyAsync(x => x.Name == name));
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
        Assert.Equal("update", actions["泵站" + u]); Assert.Equal("rename", actions["日报" + u]); Assert.Equal("skip", actions.Single(x => x.Key.StartsWith($"@双倍流量{u}|")).Value);
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

    private static JsonObject[] Rules(JsonObject package, string message) => package["sections"]!["alarmRules"]!.AsArray().OfType<JsonObject>().Where(x => x["data"]!["message"]?.GetValue<string>() == message).ToArray();

    [Fact]
    public async Task AlarmRuleKeysAreStableAndLegacyOrdinalPackagesStillMatch()
    {
        var admin = await _site.ClientAsync("admin");
        var u = U();
        var tag = (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "规则点" + u)))).GetProperty("id").GetInt32();
        object Rule(double threshold, string message) => new { tagId = tag, type = "High", threshold, level = "Warning", delaySec = 0, recoverDeadband = 0, enabled = true, message };
        var first = (await Ok(await admin.PostAsJsonAsync("/api/alarm-rules", Rule(50, "一" + u)))).GetProperty("id").GetInt32();
        await Ok(await admin.PostAsJsonAsync("/api/alarm-rules", Rule(60, "二" + u)));
        var before = await ExportAsync(admin);
        var secondKey = Rules(before, "二" + u).Single()["key"]!.GetValue<string>();
        Assert.StartsWith($"A区设备/规则点{u}|", secondKey); Assert.DoesNotContain("|High|", secondKey);

        // 旧格式包（无稳定键、按 测点|类型|序号）：与当前库按序号对应，无差异。
        var legacy = (JsonObject)before.DeepClone();
        foreach (var item in legacy["sections"]!["alarmRules"]!.AsArray().OfType<JsonObject>())
        {
            var data = item["data"]!.AsObject(); data.Remove("key");
        }
        var n = 0;
        foreach (var item in Rules(legacy, "一" + u).Concat(Rules(legacy, "二" + u))) item["key"] = $"A区设备/规则点{u}|High|{++n}";
        foreach (var item in legacy["sections"]!["alarmRules"]!.AsArray().OfType<JsonObject>().Where(x => !x["key"]!.GetValue<string>().Contains($"规则点{u}|")))
            item["key"] = item["key"]!.GetValue<string>().Split('|')[0] + "|" + item["data"]!["type"]!.GetValue<string>() + "|1";
        var legacyPreview = await PreviewAsync(admin, legacy, "Overwrite");
        Assert.DoesNotContain(legacyPreview.GetProperty("entries").EnumerateArray(), x => x.GetProperty("section").GetString() == "alarmRules" && x.GetProperty("key").GetString()!.Contains($"规则点{u}"));

        // 删除第一条后，第二条的键不变（旧序号方案下会从 2 变为 1）。
        Assert.True((await admin.DeleteAsync($"/api/alarm-rules/{first}")).IsSuccessStatusCode);
        var after = await ExportAsync(admin);
        Assert.Equal(secondKey, Rules(after, "二" + u).Single()["key"]!.GetValue<string>());
        var diff = await Ok(await admin.PostAsJsonAsync("/api/project/import/preview", new { package = before, resolution = "Overwrite" }));
        var ruleEntries = diff.GetProperty("entries").EnumerateArray().Where(x => x.GetProperty("section").GetString() == "alarmRules" && x.GetProperty("key").GetString()!.Contains($"规则点{u}")).ToList();
        Assert.Equal("add", Assert.Single(ruleEntries).GetProperty("action").GetString()); // 只补回被删的那条，第二条不被误判为修改

        // 冲突选择“改名”：作为另一条规则以新键新增。
        var changed = (JsonObject)after.DeepClone(); Rules(changed, "二" + u).Single()["data"]!["threshold"] = 66;
        var renamed = await PreviewAsync(admin, changed, "Rename");
        var entry = renamed.GetProperty("entries").EnumerateArray().Single(x => x.GetProperty("key").GetString() == secondKey);
        Assert.Equal("rename", entry.GetProperty("action").GetString()); Assert.NotEqual(secondKey, entry.GetProperty("finalKey").GetString());
        await using var db = await Factory().CreateDbContextAsync();
        Assert.All(await db.AlarmRules.Where(x => x.TagId == tag).ToListAsync(), x => Assert.Equal(32, x.Key.Length));
    }

    [Fact]
    public async Task PreviewsHaveNoRuntimeOrOutOfTransactionSideEffects()
    {
        var admin = await _site.ClientAsync("admin");
        var v1 = (await Ok(await admin.PostAsJsonAsync("/api/project/versions", new { note = "副作用基线" }))).GetProperty("id").GetInt32();
        var u = await SeedAsync(admin);
        await Ok(await admin.PostAsJsonAsync("/api/alarm-rules", new { tagId = _site.TagA, type = "High", threshold = 1, level = "Warning", delaySec = 0, recoverDeadband = 0, enabled = true, message = "副作用" + u }));
        var package = await ExportAsync(admin);
        // 一个会动到所有分区的包：新区域/角色、改设备、加测点、改计算、删画面引用等。
        package["sections"]!["areas"]!.AsArray().Add(new JsonObject { ["key"] = "新区" + u, ["data"] = new JsonObject { ["name"] = "新区" + u, ["description"] = "" } });
        package["sections"]!["roles"]!.AsArray().Add(new JsonObject { ["key"] = "新角色" + u, ["data"] = new JsonObject { ["name"] = "新角色" + u, ["description"] = "", ["permissions"] = "View" } });
        Item(package, "devices", "A区设备")["data"]!["pollIntervalMs"] = 4321;
        foreach (var rule in package["sections"]!["alarmRules"]!.AsArray().OfType<JsonObject>()) rule["data"]!["threshold"] = 99;

        var reloader = _site.Services.GetRequiredService<Scada.Server.Services.ConfigReloader>();
        var engine = _site.Services.GetRequiredService<Scada.Engine.Calculations.CalcJobEngine>();
        var models = _site.Services.GetRequiredService<AccessModelProvider>();
        var snapshot = _site.Services.GetRequiredService<Scada.Core.Interfaces.IConfigSnapshot>();
        async Task<object> StateAsync()
        {
            await using var db = await Factory().CreateDbContextAsync();
            await using var runtime = await _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>().CreateDbContextAsync();
            return new
            {
                reloads = reloader.ReloadCount, engine = engine.ReloadRequestCount, generation = models.Generation,
                logs = await db.OperationLogs.CountAsync(), versions = await db.ConfigVersions.CountAsync(), events = await runtime.AlarmEvents.CountAsync(x => x.EndTs == null),
                areas = await db.Areas.CountAsync(), tags = snapshot.Tags.Count, snapshotVersion = snapshot.Version, sha = (await Ok(await admin.PostAsync($"/api/project/versions/{v1}/rollback/preview", null))).GetProperty("baseSha").GetString()
            };
        }
        var cfg = await _site.ClientAsync("cfgA"); // 登录本身会写审计，放在取基线之前
        var state = System.Text.Json.JsonSerializer.Serialize(await StateAsync());
        var preview = await PreviewAsync(admin, package, "Overwrite");
        Assert.Empty(preview.GetProperty("errors").EnumerateArray());
        Assert.Contains(preview.GetProperty("entries").EnumerateArray(), x => x.GetProperty("section").GetString() == "roles");
        var denied = await PreviewAsync(cfg, package, "Overwrite"); // 越权项只在预览结果中列出，不写拒绝审计
        Assert.NotEmpty(denied.GetProperty("forbidden").EnumerateArray());
        await Ok(await admin.PostAsync($"/api/project/versions/{v1}/rollback/preview", null)); // 镜像预览：删除计算任务（关闭报警事件）、删画面等
        var now = System.Text.Json.JsonSerializer.Serialize(await StateAsync());
        Assert.Equal(state, now);
    }

    [Fact]
    public async Task ExcludedOperationalStateIsPreservedAcrossPreviewAndPublish()
    {
        var admin = await _site.ClientAsync("admin");
        var u = U();
        var rule = (await Ok(await admin.PostAsJsonAsync("/api/alarm-rules", new { tagId = _site.TagA, type = "Low", threshold = -5, level = "Warning", delaySec = 0, recoverDeadband = 0, enabled = true, message = "停用" + u }))).GetProperty("id").GetInt32();
        var package = await ExportAsync(admin);
        Rules(package, "停用" + u).Single()["data"]!["threshold"] = -6;
        var preview = await PreviewAsync(admin, package, "Overwrite");
        // 预览之后有人把规则停用：停用状态不入包也不入冲突哈希，发布不报 409，且停用状态被保留。
        await Ok(await admin.PostAsJsonAsync($"/api/alarm-rules/{rule}/out-of-service", new { outOfService = true, reason = "检修" }));
        await Ok(await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Overwrite", baseSha = preview.GetProperty("baseSha").GetString() }));
        await using var db = await Factory().CreateDbContextAsync();
        var saved = await db.AlarmRules.SingleAsync(x => x.Id == rule);
        Assert.Equal(-6, saved.Threshold); Assert.True(saved.OutOfService); Assert.Equal("检修", saved.OutOfServiceReason);
    }

    [Fact]
    public async Task ScreenPublishSnapshotFailureIsReportedInResultAndHealth()
    {
        var site = new AuthorizationTests.Site(); await site.InitializeAsync();
        try
        {
            var admin = await site.ClientAsync("admin");
            var screen = (await Ok(await admin.PostAsJsonAsync("/api/screens", new { name = "快照失败画面" }))).GetProperty("id").GetInt32();
            await using (var db = await Factory(site).CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("ALTER TABLE ConfigVersions RENAME TO ConfigVersionsBroken");
            var published = await Ok(await admin.PostAsJsonAsync($"/api/screens/{screen}/publish", new { revision = 1 }));
            Assert.Equal(JsonValueKind.Null, published.GetProperty("versionId").ValueKind);
            Assert.Contains("快照失败", published.GetProperty("warning").GetString());
            var health = await Ok(await admin.GetAsync("/api/health"));
            Assert.Contains(health.GetProperty("warnings").EnumerateArray(), x => x.GetProperty("code").GetString() == "config_snapshot_failed");
            await using (var db = await Factory(site).CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("ALTER TABLE ConfigVersionsBroken RENAME TO ConfigVersions");
            await Ok(await admin.PostAsJsonAsync("/api/project/versions", new { note = "补快照" }));
            health = await Ok(await admin.GetAsync("/api/health"));
            Assert.DoesNotContain(health.GetProperty("warnings").EnumerateArray(), x => x.GetProperty("code").GetString() == "config_snapshot_failed");
        }
        finally { await site.DisposeAsync(); }
    }
}
