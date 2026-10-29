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

public sealed class HmiLayoutProjectTests(AuthorizationTests.Site site) : IClassFixture<AuthorizationTests.Site>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IDbContextFactory<ConfigDbContext> Factory(AuthorizationTests.Site? target = null) => (target ?? site).Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static string U() => Guid.NewGuid().ToString("N")[..6];
    private static JsonObject Doc(int? screenId, string title) => JsonSerializer.SerializeToNode(new HmiLayoutDocument
    {
        Monitors = [new HmiMonitor { Name = "主屏", PreviewWidth = 1280, PreviewHeight = 1024, Regions = [new HmiRegion { Id = "main", ScreenId = screenId, Title = title }] }]
    }, Json)!.AsObject();
    private static JsonObject Layout(JsonObject package, string name) => package["sections"]!["hmiLayouts"]!.AsArray()
        .OfType<JsonObject>().Single(x => x["key"]!.GetValue<string>() == name)["data"]!.AsObject();
    private static JsonObject Region(JsonNode document) => document["monitors"]![0]!["regions"]![0]!.AsObject();
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task<JsonObject> Export(HttpClient client) => JsonNode.Parse(await (await client.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();
    private static async Task<JsonElement> Preview(HttpClient client, JsonObject package) => await Ok(await client.PostAsJsonAsync("/api/project/import/preview", new { package, resolution = "Overwrite" }));
    private static async Task Apply(HttpClient client, JsonObject package)
    {
        var preview = await Preview(client, package);
        Assert.Empty(preview.GetProperty("errors").EnumerateArray());
        await Ok(await client.PostAsJsonAsync("/api/project/import", new { package, resolution = "Overwrite", baseSha = preview.GetProperty("baseSha").GetString() }));
    }
    private async Task<(int ScreenId, int LayoutId, string Name, string PublishedName, string DraftOnly)> Seed()
    {
        var suffix = U();
        var document = JsonSerializer.Serialize(new ScreenDocument(), Json);
        await using var db = await Factory().CreateDbContextAsync();
        var screen = new ProcessScreen { Name = "工程画面" + suffix, PublishedName = "运行画面" + suffix, DraftJson = document, PublishedJson = document, PublishedRevision = 1 };
        db.ProcessScreens.Add(screen); await db.SaveChangesAsync();
        var layout = new HmiLayout
        {
            Name = "工程布局" + suffix, DocumentJson = Doc(null, "未完成草稿").ToJsonString(), Revision = 5,
            PublishedName = "运行布局" + suffix, PublishedJson = Doc(screen.Id, "原运行版").ToJsonString(), PublishedRevision = 3,
            PublishedTs = 123, PublishedBy = "original"
        };
        var draftOnly = "未发布布局" + suffix;
        db.HmiLayouts.AddRange(layout, new HmiLayout { Name = draftOnly, DocumentJson = Doc(null, "草稿专用").ToJsonString() });
        await db.SaveChangesAsync();
        db.HmiLayoutVersions.Add(new HmiLayoutVersion { LayoutId = layout.Id, Revision = 3, Name = layout.PublishedName, DocumentJson = layout.PublishedJson, PublishedTs = 123, PublishedBy = "original", Note = "初始发布" });
        await db.SaveChangesAsync();
        return (screen.Id, layout.Id, layout.Name, layout.PublishedName, draftOnly);
    }

    [Fact]
    public async Task PackageRoundTripSeparatesDraftAndPublishedAndRemapsScreenReferences()
    {
        var admin = await site.ClientAsync("admin"); var seeded = await Seed();
        await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name = "project-ws-" + U(), layoutId = seeded.LayoutId }));
        var package = await Export(admin); var source = Layout(package, seeded.Name);
        Assert.False(source.ContainsKey("document"));
        Assert.Null(Region(source["draft"]!)["screenRef"]);
        Assert.Equal("未完成草稿", Region(source["draft"]!)["title"]!.GetValue<string>());
        Assert.Equal(seeded.PublishedName, source["publishedName"]!.GetValue<string>());
        Assert.DoesNotContain("workstations", package["sections"]!.AsObject().Select(x => x.Key), StringComparer.OrdinalIgnoreCase);

        var target = new AuthorizationTests.Site(); await target.InitializeAsync();
        try
        {
            var other = await target.ClientAsync("admin");
            // 目标库画面编号与源库错开，验证运行快照也按名称映射。
            for (var i = 0; i <= seeded.ScreenId; i++) await Ok(await other.PostAsJsonAsync("/api/screens", new { name = "占位" + i }));
            await Apply(other, package);
            await using var db = await Factory(target).CreateDbContextAsync();
            var layout = await db.HmiLayouts.SingleAsync(x => x.Name == seeded.Name);
            var screen = await db.ProcessScreens.SingleAsync(x => x.Name == Region(source["published"]!)["screenRef"]!.GetValue<string>());
            Assert.NotEqual(seeded.ScreenId, screen.Id);
            Assert.Equal(screen.Id, Region(JsonNode.Parse(layout.PublishedJson!)!)["screenId"]!.GetValue<int>());
            Assert.Null(Region(JsonNode.Parse(layout.DocumentJson)!)["screenId"]);
            Assert.Equal(1280, JsonNode.Parse(layout.DocumentJson)!["monitors"]![0]!["previewWidth"]!.GetValue<int>());
            Assert.Equal(1024, JsonNode.Parse(layout.PublishedJson!)!["monitors"]![0]!["previewHeight"]!.GetValue<int>());
            Assert.Equal("原运行版", Region(JsonNode.Parse(layout.PublishedJson!)!)["title"]!.GetValue<string>());
            var history = await db.HmiLayoutVersions.SingleAsync(x => x.LayoutId == layout.Id);
            Assert.Equal("import", history.Kind); Assert.Equal(layout.PublishedRevision, history.Revision);
            var unpublished = await db.HmiLayouts.SingleAsync(x => x.Name == seeded.DraftOnly);
            Assert.Null(unpublished.PublishedJson); Assert.Null(unpublished.PublishedRevision);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/hmi/layouts/{unpublished.Id}")).StatusCode);
            Assert.Empty(await db.HmiWorkstations.ToListAsync());
            Assert.Empty((await Preview(other, package)).GetProperty("entries").EnumerateArray());
        }
        finally { await target.DisposeAsync(); }
    }

    [Fact]
    public async Task LegacyDocumentImportsAsDraftAndPublishedAndRemainsComparable()
    {
        var admin = await site.ClientAsync("admin"); var seeded = await Seed(); var package = await Export(admin);
        var legacyName = "旧格式布局" + U();
        var document = Layout(package, seeded.Name)["published"]!.DeepClone();
        package["sections"]!["hmiLayouts"]!.AsArray().Add(new JsonObject
        {
            ["key"] = legacyName, ["data"] = new JsonObject { ["name"] = legacyName, ["document"] = document }
        });
        await Apply(admin, package);
        await using var db = await Factory().CreateDbContextAsync();
        var imported = await db.HmiLayouts.SingleAsync(x => x.Name == legacyName);
        Assert.Equal(imported.DocumentJson, imported.PublishedJson); Assert.Equal(legacyName, imported.PublishedName);
        Assert.Single(await db.HmiLayoutVersions.Where(x => x.LayoutId == imported.Id).ToListAsync());
        Assert.DoesNotContain((await Preview(admin, package)).GetProperty("entries").EnumerateArray(), x => x.GetProperty("section").GetString() == "hmiLayouts");
    }

    [Fact]
    public async Task DraftImportsAllowUnpublishedScreensAndClearMissingReferencesWithWarning()
    {
        var admin = await site.ClientAsync("admin"); var seeded = await Seed();
        var unpublishedName = "未发布画面" + U();
        var screen = await Ok(await admin.PostAsJsonAsync("/api/screens", new { name = unpublishedName }));
        var unpublishedId = screen.GetProperty("id").GetInt32();
        await using (var source = await Factory().CreateDbContextAsync())
        {
            var brokenDraft = await source.HmiLayouts.SingleAsync(x => x.Name == seeded.DraftOnly);
            brokenDraft.DocumentJson = Doc(int.MaxValue, "待修复").ToJsonString();
            await source.SaveChangesAsync();
        }
        var package = await Export(admin);
        // 让原布局也成为导入动作，覆盖已有草稿中的无效引用，而非仅新增副本。
        await using (var source = await Factory().CreateDbContextAsync())
        {
            var draft = await source.HmiLayouts.SingleAsync(x => x.Name == seeded.DraftOnly);
            draft.DocumentJson = Doc(null, "导入前草稿").ToJsonString();
            await source.SaveChangesAsync();
        }
        Region(Layout(package, seeded.Name)["draft"]!)["screenRef"] = unpublishedName;
        var missingName = "失效引用草稿" + U(); var missingDoc = Layout(package, seeded.DraftOnly)["draft"]!.DeepClone();
        Assert.Equal($"#缺失:{int.MaxValue}", Region(missingDoc)["screenRef"]!.GetValue<string>());
        package["sections"]!["hmiLayouts"]!.AsArray().Add(new JsonObject
        {
            ["key"] = missingName, ["data"] = new JsonObject { ["name"] = missingName, ["draft"] = missingDoc, ["published"] = null }
        });
        var preview = await Preview(admin, package);
        Assert.Empty(preview.GetProperty("errors").EnumerateArray());
        Assert.Contains(preview.GetProperty("warnings").EnumerateArray(), x => x.GetString()!.Contains("草稿引用的画面") && x.GetString()!.Contains("已清空"));
        await Apply(admin, package);
        await using var db = await Factory().CreateDbContextAsync();
        var updated = await db.HmiLayouts.SingleAsync(x => x.Id == seeded.LayoutId);
        Assert.Equal(unpublishedId, Region(JsonNode.Parse(updated.DocumentJson)!)["screenId"]!.GetValue<int>());
        Assert.Equal(3, updated.PublishedRevision);
        Assert.Single(await db.HmiLayoutVersions.Where(x => x.LayoutId == seeded.LayoutId).ToListAsync());
        var missing = await db.HmiLayouts.SingleAsync(x => x.Name == missingName);
        Assert.Null(Region(JsonNode.Parse(missing.DocumentJson)!)["screenId"]); Assert.Null(missing.PublishedJson);
    }

    [Fact]
    public async Task SnapshotRollbackRestoresBothStatesAndAppendsPublicationHistory()
    {
        var admin = await site.ClientAsync("admin"); var seeded = await Seed();
        var station = await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name = "retained-ws-" + U(), layoutId = seeded.LayoutId, allowControl = false }));
        var snapshot = await Ok(await admin.PostAsJsonAsync("/api/project/versions", new { note = "布局基线" }));
        var versionId = snapshot.GetProperty("id").GetInt32();
        var package = await Export(admin); var data = Layout(package, seeded.Name);
        Region(data["draft"]!)["title"] = "新草稿"; Region(data["published"]!)["title"] = "新运行版";
        var initiallyDraftOnly = Layout(package, seeded.DraftOnly);
        initiallyDraftOnly["published"] = data["published"]!.DeepClone(); initiallyDraftOnly["publishedName"] = seeded.DraftOnly;
        await Apply(admin, package);
        int importedRevision;
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var imported = await db.HmiLayouts.SingleAsync(x => x.Id == seeded.LayoutId);
            importedRevision = imported.PublishedRevision!.Value;
            Assert.Equal(6, importedRevision); Assert.Equal(2, await db.HmiLayoutVersions.CountAsync(x => x.LayoutId == seeded.LayoutId));
        }
        // 只改草稿的工程导入不能改动运行版本或重复生成发布历史。
        var draftChange = await Export(admin); Region(Layout(draftChange, seeded.Name)["draft"]!)["title"] = "再次修改草稿";
        await Apply(admin, draftChange);
        await using (var db = await Factory().CreateDbContextAsync())
        {
            Assert.Equal(importedRevision, (await db.HmiLayouts.SingleAsync(x => x.Id == seeded.LayoutId)).PublishedRevision);
            Assert.Equal(2, await db.HmiLayoutVersions.CountAsync(x => x.LayoutId == seeded.LayoutId));
        }
        var preview = await Ok(await admin.PostAsync($"/api/project/versions/{versionId}/rollback/preview", null));
        Assert.Empty(preview.GetProperty("errors").EnumerateArray());
        await Ok(await admin.PostAsJsonAsync($"/api/project/versions/{versionId}/rollback", new { baseSha = preview.GetProperty("baseSha").GetString() }));
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var restored = await db.HmiLayouts.SingleAsync(x => x.Id == seeded.LayoutId);
            Assert.Equal("未完成草稿", Region(JsonNode.Parse(restored.DocumentJson)!)["title"]!.GetValue<string>());
            Assert.Equal("原运行版", Region(JsonNode.Parse(restored.PublishedJson!)!)["title"]!.GetValue<string>());
            Assert.True(restored.PublishedRevision > importedRevision);
            Assert.Equal(3, await db.HmiLayoutVersions.CountAsync(x => x.LayoutId == restored.Id));
            Assert.Equal(123, (await db.HmiLayoutVersions.SingleAsync(x => x.LayoutId == restored.Id && x.Revision == 3)).PublishedTs);
            var unpublished = await db.HmiLayouts.SingleAsync(x => x.Name == seeded.DraftOnly);
            Assert.Null(unpublished.PublishedJson); Assert.Null(unpublished.PublishedRevision);
            Assert.Single(await db.HmiLayoutVersions.Where(x => x.LayoutId == unpublished.Id).ToListAsync());
            var retained = await db.HmiWorkstations.SingleAsync(x => x.Id == station.GetProperty("id").GetInt32());
            Assert.Equal(seeded.LayoutId, retained.LayoutId); Assert.False(retained.AllowControl);
        }
        var runtime = await Ok(await admin.GetAsync($"/api/hmi/layouts/{seeded.LayoutId}"));
        Assert.Equal("原运行版", runtime.GetProperty("document").GetProperty("monitors")[0].GetProperty("regions")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ImportRejectsUnpublishedScreenReferencesAndRollsBackPublicationHistory()
    {
        var admin = await site.ClientAsync("admin"); var seeded = await Seed(); var package = await Export(admin);
        var screenName = Region(Layout(package, seeded.Name)["published"]!)["screenRef"]!.GetValue<string>();
        var screen = package["sections"]!["screens"]!.AsArray().OfType<JsonObject>().Single(x => x["key"]!.GetValue<string>() == screenName);
        screen["data"]!["published"] = null; screen["data"]!["publishedName"] = null;
        // 布局本身未变，最终整体校验仍须保护已发布布局。
        var preview = await Preview(admin, package);
        Assert.Contains(preview.GetProperty("errors").EnumerateArray(), x => x.GetString()!.Contains("已发布的画面"));
        var response = await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Overwrite", baseSha = preview.GetProperty("baseSha").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = await Factory().CreateDbContextAsync();
        Assert.NotNull((await db.ProcessScreens.SingleAsync(x => x.Id == seeded.ScreenId)).PublishedJson);
        Assert.Equal(3, (await db.HmiLayouts.SingleAsync(x => x.Id == seeded.LayoutId)).PublishedRevision);
        Assert.Single(await db.HmiLayoutVersions.Where(x => x.LayoutId == seeded.LayoutId).ToListAsync());
    }
}
