using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class HmiLayoutLifecycleTests(AuthorizationTests.Site site) : IClassFixture<AuthorizationTests.Site>
{
    private static string Name() => "布局生命周期-" + Guid.NewGuid().ToString("N");
    private IDbContextFactory<ConfigDbContext> Factory => site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static int Id(JsonElement x) => x.GetProperty("id").GetInt32();
    private static int Revision(JsonElement x) => x.GetProperty("revision").GetInt32();
    private static HmiLayoutDocument Document(params HmiRegion[] regions) => new()
    {
        Monitors = [new HmiMonitor { Name = "主屏", Columns = 12, Rows = 12, Regions = regions.ToList() }]
    };
    private static HmiRegion Region(string id = "main", string kind = "navigation", int? screenId = null) =>
        new() { Id = id, Kind = kind, ScreenId = screenId, RowSpan = 12, ColSpan = 12 };
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task<JsonElement> Create(HttpClient client, HmiLayoutDocument? document = null) =>
        await Ok(await client.PostAsJsonAsync("/api/hmi/layouts", new { name = Name(), document = document ?? Document(Region()) }));
    private static async Task<JsonElement> Publish(HttpClient client, JsonElement layout, string note = "发布经过检查的布局") =>
        await Ok(await client.PostAsJsonAsync($"/api/hmi/layouts/{Id(layout)}/publish", new { revision = Revision(layout), note }));
    private static async Task<JsonElement> Save(HttpClient client, JsonElement layout, string name, HmiLayoutDocument document) =>
        await Ok(await client.PutAsJsonAsync($"/api/hmi/layouts/{Id(layout)}", new { name, document, revision = Revision(layout) }));

    [Fact]
    public async Task IncompleteDraftsStayOutOfRuntimeAndRequireGlobalConfigure()
    {
        var admin = await site.ClientAsync("admin"); var op = await site.ClientAsync("opA"); var areaConfigurer = await site.ClientAsync("cfgA");
        var draft = await Create(admin, Document()); var id = Id(draft);
        Assert.True(draft.GetProperty("hasDraftChanges").GetBoolean());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("publishedRevision").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/hmi/layouts/{id}")).StatusCode);
        var runtime = await Ok(await op.GetAsync("/api/hmi/layouts"));
        Assert.DoesNotContain(runtime.EnumerateArray(), x => Id(x) == id);
        Assert.Contains((await Ok(await admin.GetAsync("/api/hmi/layouts?draft=true"))).EnumerateArray(), x => Id(x) == id);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/hmi/layouts?draft=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await areaConfigurer.GetAsync($"/api/hmi/layouts/{id}?draft=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync($"/api/hmi/layouts/{id}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync($"/api/hmi/layouts/{id}/versions/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/hmi/layouts/{id}/publish", new { revision = Revision(draft), note = "无配置权限" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/hmi/layouts/{id}/rollback", new { revision = Revision(draft), targetRevision = 1, note = "无配置权限" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/hmi/layouts/{id}/publish", new { revision = Revision(draft), note = "空布局不能发布" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/hmi/workstations", new { name = "lifecycle-" + Guid.NewGuid().ToString("N"), layoutId = id })).StatusCode);
        Assert.Empty((await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}/versions"))).EnumerateArray());
    }

    [Fact]
    public async Task WorkstationsRequirePublicationAndLegacyAssignmentsCanBeCleared()
    {
        var admin = await site.ClientAsync("admin"); var draft = await Create(admin); var id = Id(draft);
        var name = "preview-ws-" + Guid.NewGuid().ToString("N");
        var rejected = await admin.PostAsJsonAsync("/api/hmi/workstations", new { name, layoutId = id });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("已发布", await rejected.Content.ReadAsStringAsync());
        await using (var db = await Factory.CreateDbContextAsync()) Assert.False(await db.HmiWorkstations.AnyAsync(x => x.Name == name));
        var station = await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name, layoutId = (int?)null, allowControl = false }));
        var stationId = Id(station);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/hmi/workstations/{stationId}", new { name, layoutId = id, allowControl = true, expectedUpdatedTs = station.GetProperty("updatedTs").GetInt64() })).StatusCode);
        // Existing invalid assignments are retained for explicit repair, never silently rebound.
        await using (var db = await Factory.CreateDbContextAsync())
        {
            var legacy = await db.HmiWorkstations.SingleAsync(x => x.Id == stationId);
            Assert.Null(legacy.LayoutId); Assert.False(legacy.AllowControl);
            legacy.LayoutId = id; await db.SaveChangesAsync();
        }
        var resolved = await Ok(await admin.GetAsync($"/api/hmi/workstations/resolve?id={stationId}"));
        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("layout").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/hmi/workstations/{stationId}", new { name, layoutId = id, expectedUpdatedTs = station.GetProperty("updatedTs").GetInt64() })).StatusCode);
        station = await Ok(await admin.PutAsJsonAsync($"/api/hmi/workstations/{stationId}", new { name, layoutId = (int?)null, expectedUpdatedTs = station.GetProperty("updatedTs").GetInt64() }));
        await Publish(admin, draft);
        station = await Ok(await admin.PutAsJsonAsync($"/api/hmi/workstations/{stationId}", new { name, layoutId = id, expectedUpdatedTs = station.GetProperty("updatedTs").GetInt64() }));
        Assert.Equal(id, Id((await Ok(await admin.GetAsync($"/api/hmi/workstations/resolve?id={stationId}"))).GetProperty("layout")));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/hmi/workstations/{stationId}", new { name, layoutId = int.MaxValue, expectedUpdatedTs = station.GetProperty("updatedTs").GetInt64() })).StatusCode);
    }

    [Fact]
    public async Task LayoutDeletionRequiresTheReviewedRevisionAndInvalidatesAssignedStationSnapshots()
    {
        var admin = await site.ClientAsync("admin"); var draft = await Create(admin); var published = await Publish(admin, draft); var id = Id(published);
        var name = "delete-" + Guid.NewGuid().ToString("N");
        var station = await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name, layoutId = id, allowControl = false }));
        var stamp = station.GetProperty("updatedTs").GetInt64(); var stationId = Id(station);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/hmi/layouts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/hmi/layouts/{id}?revision={Revision(draft)}")).StatusCode);
        Assert.Single((await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}/versions"))).EnumerateArray());
        Assert.Equal(id, (await Ok(await admin.GetAsync($"/api/hmi/workstations/resolve?id={stationId}"))).GetProperty("workstation").GetProperty("layoutId").GetInt32());
        await Ok(await admin.DeleteAsync($"/api/hmi/layouts/{id}?revision={Revision(published)}"));
        var cleared = (await Ok(await admin.GetAsync($"/api/hmi/workstations/resolve?id={stationId}"))).GetProperty("workstation");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("layoutId").ValueKind);
        Assert.True(cleared.GetProperty("updatedTs").GetInt64() > stamp);
        Assert.False(cleared.GetProperty("allowControl").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/hmi/workstations/{stationId}", new { name, layoutId = (int?)null, allowControl = true, expectedUpdatedTs = stamp })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/hmi/workstations/{stationId}?updatedTs={stamp}")).StatusCode);
        await Ok(await admin.DeleteAsync($"/api/hmi/workstations/{stationId}?updatedTs={cleared.GetProperty("updatedTs").GetInt64()}"));
    }

    [Fact]
    public async Task StationUpdatesAndDeletesRequireAFreshStampAndNeverOverwriteNewerPermissions()
    {
        var admin = await site.ClientAsync("admin"); var name = "concurrency-" + Guid.NewGuid().ToString("N");
        var first = await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name, allowControl = true }));
        var id = Id(first); var stamp = first.GetProperty("updatedTs").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/hmi/workstations/{id}", new { name, allowControl = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/hmi/workstations/{id}")).StatusCode);
        var current = await Ok(await admin.PutAsJsonAsync($"/api/hmi/workstations/{id}", new { name, allowControl = false, description = "最新权限", expectedUpdatedTs = stamp }));
        Assert.True(current.GetProperty("updatedTs").GetInt64() > stamp);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/hmi/workstations/{id}", new { name, allowControl = true, description = "过期窗口", expectedUpdatedTs = stamp })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/hmi/workstations/{id}?updatedTs={stamp}")).StatusCode);
        var resolved = (await Ok(await admin.GetAsync($"/api/hmi/workstations/resolve?id={id}"))).GetProperty("workstation");
        Assert.False(resolved.GetProperty("allowControl").GetBoolean()); Assert.Equal("最新权限", resolved.GetProperty("description").GetString());
        // A monotonic stamp also handles two updates in one millisecond or a clock moving backward.
        await using (var db = await Factory.CreateDbContextAsync()) { var row = await db.HmiWorkstations.SingleAsync(x => x.Id == id); row.UpdatedTs += 100_000; await db.SaveChangesAsync(); stamp = row.UpdatedTs; }
        current = await Ok(await admin.PutAsJsonAsync($"/api/hmi/workstations/{id}", new { name, allowControl = false, expectedUpdatedTs = stamp }));
        Assert.True(current.GetProperty("updatedTs").GetInt64() > stamp);
    }

    [Fact]
    public async Task StationStampIsEnforcedByTheDatabaseUpdatePredicate()
    {
        var admin = await site.ClientAsync("admin"); var name = "tracked-" + Guid.NewGuid().ToString("N");
        var created = await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name, allowControl = false })); var id = Id(created);
        await using var first = await Factory.CreateDbContextAsync(); await using var second = await Factory.CreateDbContextAsync();
        var latest = await first.HmiWorkstations.SingleAsync(x => x.Id == id); var stale = await second.HmiWorkstations.SingleAsync(x => x.Id == id);
        latest.Description = "最新修改"; latest.UpdatedTs++; await first.SaveChangesAsync();
        stale.AllowControl = true; stale.UpdatedTs += 2;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Theory]
    [InlineData(320, 240, true)]
    [InlineData(7680, 4320, true)]
    [InlineData(null, null, true)]
    [InlineData(319, 1080, false)]
    [InlineData(1920, 239, false)]
    [InlineData(7681, 1080, false)]
    [InlineData(1920, 4321, false)]
    [InlineData(null, 1080, false)]
    public async Task PreviewDimensionsAreValidatedAndPreservedInPublication(int? width, int? height, bool valid)
    {
        var admin = await site.ClientAsync("admin"); var document = Document(Region());
        document.Monitors[0].PreviewWidth = width; document.Monitors[0].PreviewHeight = height;
        var response = await admin.PostAsJsonAsync("/api/hmi/layouts", new { name = Name(), document });
        if (!valid) { Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); return; }
        var published = await Publish(admin, await Ok(response));
        var runtime = await Ok(await admin.GetAsync($"/api/hmi/layouts/{Id(published)}"));
        var monitor = runtime.GetProperty("document").GetProperty("monitors")[0];
        Assert.Equal(width, monitor.GetProperty("previewWidth").Deserialize<int?>());
        Assert.Equal(height, monitor.GetProperty("previewHeight").Deserialize<int?>());
    }

    [Fact]
    public async Task SavingDraftNameAndDocumentDoesNotChangePublishedLayout()
    {
        var admin = await site.ClientAsync("admin"); var op = await site.ClientAsync("opA");
        var initial = await Create(admin); var published = await Publish(admin, initial);
        var id = Id(initial); var publishedName = initial.GetProperty("name").GetString()!;
        Assert.False(published.GetProperty("hasDraftChanges").GetBoolean());
        var draft = await Save(admin, published, Name(), Document(Region(kind: "alarmList")));
        Assert.True(draft.GetProperty("hasDraftChanges").GetBoolean());
        var running = await Ok(await op.GetAsync($"/api/hmi/layouts/{id}"));
        Assert.Equal(publishedName, running.GetProperty("name").GetString());
        Assert.Equal(Revision(published), Revision(running));
        Assert.Equal("navigation", running.GetProperty("document").GetProperty("monitors")[0].GetProperty("regions")[0].GetProperty("kind").GetString());
        var saved = await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}?draft=true"));
        Assert.Equal(Revision(draft), Revision(saved));
        Assert.Equal("alarmList", saved.GetProperty("document").GetProperty("monitors")[0].GetProperty("regions")[0].GetProperty("kind").GetString());
        var station = await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name = "isolate-" + Guid.NewGuid().ToString("N"), layoutId = id }));
        Assert.Equal(publishedName, (await Ok(await op.GetAsync($"/api/hmi/workstations/resolve?id={Id(station)}"))).GetProperty("layout").GetProperty("name").GetString());
        var second = await Publish(admin, draft);
        Assert.False(second.GetProperty("hasDraftChanges").GetBoolean());
        Assert.Equal(draft.GetProperty("name").GetString(), (await Ok(await op.GetAsync($"/api/hmi/layouts/{id}"))).GetProperty("name").GetString());
        var versions = await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}/versions"));
        Assert.Equal(2, versions.GetArrayLength());
        Assert.False(versions[0].TryGetProperty("document", out _));
        Assert.NotNull(await site.LastLogAsync(x => x.Action.StartsWith("发布运行布局") && x.Action.Contains(publishedName)));
    }

    [Fact]
    public async Task RollbackCreatesNewPublicationAndKeepsSavedDraft()
    {
        var admin = await site.ClientAsync("admin");
        var first = await Publish(admin, await Create(admin)); var id = Id(first);
        var second = await Publish(admin, await Save(admin, first, Name(), Document(Region(kind: "alarmList"))));
        var draft = await Save(admin, second, Name(), Document()); // 保留尚未完成的当前草稿。
        var rolled = await Ok(await admin.PostAsJsonAsync($"/api/hmi/layouts/{id}/rollback", new { revision = Revision(draft), targetRevision = Revision(first), note = "恢复上一个运行方案" }));
        Assert.True(Revision(rolled) > Revision(draft));
        Assert.Equal(Revision(rolled), rolled.GetProperty("publishedRevision").GetInt32());
        Assert.Equal(draft.GetProperty("name").GetString(), rolled.GetProperty("name").GetString());
        Assert.Empty(rolled.GetProperty("document").GetProperty("monitors")[0].GetProperty("regions").EnumerateArray());
        Assert.True(rolled.GetProperty("hasDraftChanges").GetBoolean());
        var running = await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}"));
        Assert.Equal(first.GetProperty("name").GetString(), running.GetProperty("name").GetString());
        Assert.Equal("navigation", running.GetProperty("document").GetProperty("monitors")[0].GetProperty("regions")[0].GetProperty("kind").GetString());
        var versions = await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}/versions"));
        Assert.Equal(3, versions.GetArrayLength());
        Assert.Equal("rollback", versions[0].GetProperty("kind").GetString());
        Assert.Equal(Revision(first), versions[0].GetProperty("restoredFromRevision").GetInt32());
        var old = await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}/versions/{Revision(second)}"));
        Assert.Equal("alarmList", old.GetProperty("document").GetProperty("monitors")[0].GetProperty("regions")[0].GetProperty("kind").GetString());
        Assert.Equal(second.GetProperty("name").GetString(), old.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/hmi/layouts/{id}/rollback", new { revision = Revision(draft), targetRevision = Revision(second), note = "过期窗口" })).StatusCode);
        Assert.NotNull(await site.LastLogAsync(x => x.Action.StartsWith("回退运行布局") && x.Action.Contains($"r{Revision(first)} → r{Revision(rolled)}")));
    }

    [Fact]
    public async Task PublishRejectsInvalidGeometryReferencesAndNotesWithoutChangingState()
    {
        var admin = await site.ClientAsync("admin");
        HmiLayoutDocument[] invalid =
        [
            Document(Region(kind: "screen")),
            Document(Region(kind: "screen", screenId: int.MaxValue)),
            Document(Region("a"), Region("b")),
            Document(new HmiRegion { Id = "overflow", Kind = "navigation", Row = int.MaxValue, RowSpan = int.MaxValue }),
            Document(Region("same"), Region("same"))
        ];
        foreach (var document in invalid)
        {
            var draft = await Create(admin, document); var id = Id(draft);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/hmi/layouts/{id}/publish", new { revision = Revision(draft), note = "应被拒绝" })).StatusCode);
            var after = await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}?draft=true"));
            Assert.Equal(Revision(draft), Revision(after));
            Assert.Equal(JsonValueKind.Null, after.GetProperty("publishedRevision").ValueKind);
            Assert.Empty((await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}/versions"))).EnumerateArray());
        }
        var valid = await Create(admin);
        foreach (var note in new[] { "", "   ", new string('x', 501) })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/hmi/layouts/{Id(valid)}/publish", new { revision = Revision(valid), note })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/hmi/layouts", new { name = Name(), document = new { monitors = new object[9] } })).StatusCode);
    }

    [Fact]
    public async Task PublishedReferencesProtectScreensAndRollbackRevalidatesOldReferences()
    {
        var admin = await site.ClientAsync("admin");
        var screen = await Ok(await admin.PostAsJsonAsync("/api/screens", new { name = Name() })); var screenId = Id(screen);
        await Ok(await admin.PostAsJsonAsync($"/api/screens/{screenId}/publish", new { revision = Revision(screen) }));
        var first = await Publish(admin, await Create(admin, Document(Region(kind: "screen", screenId: screenId)))); var id = Id(first);
        var draft = await Save(admin, first, Name(), Document(Region()));
        var screenDraft = await Ok(await admin.GetAsync($"/api/screens/{screenId}?draft=true"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/screens/{screenId}?revision={Revision(screenDraft)}")).StatusCode);
        var second = await Publish(admin, draft);
        await Ok(await admin.DeleteAsync($"/api/screens/{screenId}?revision={Revision(screenDraft)}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/hmi/layouts/{id}/rollback", new { revision = Revision(second), targetRevision = Revision(first), note = "历史画面已删除" })).StatusCode);
        Assert.Equal(Revision(second), Revision(await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}?draft=true"))));
        Assert.Equal(2, (await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}/versions"))).GetArrayLength());
    }

    [Fact]
    public async Task ConcurrentPublishAndStaleSaveCannotOverwriteACommittedPublication()
    {
        var admin = await site.ClientAsync("admin"); var draft = await Create(admin); var id = Id(draft);
        var results = await Task.WhenAll(Enumerable.Range(1, 2).Select(i => admin.PostAsJsonAsync($"/api/hmi/layouts/{id}/publish", new { revision = Revision(draft), note = $"并发发布 {i}" })));
        Assert.Single(results, x => x.IsSuccessStatusCode);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/hmi/layouts/{id}", new { name = Name(), document = Document(Region(kind: "alarmList")), revision = Revision(draft) })).StatusCode);
        var after = await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}?draft=true"));
        Assert.Equal(Revision(draft) + 1, Revision(after));
        Assert.False(after.GetProperty("hasDraftChanges").GetBoolean());
        Assert.Single((await Ok(await admin.GetAsync($"/api/hmi/layouts/{id}/versions"))).EnumerateArray());
    }

    [Fact]
    public async Task FailedHistorySaveRollsBackPublicationAndAudit()
    {
        var admin = await site.ClientAsync("admin"); var draft = await Create(admin); var id = Id(draft);
        await using var setup = await Factory.CreateDbContextAsync();
        await setup.Database.ExecuteSqlRawAsync($"CREATE TRIGGER FailLayoutPublish BEFORE INSERT ON HmiLayoutVersion WHEN NEW.LayoutId = {id} BEGIN SELECT RAISE(ABORT, 'simulated history failure'); END;");
        var logCount = await setup.OperationLogs.CountAsync();
        try
        {
            var response = await admin.PostAsJsonAsync($"/api/hmi/layouts/{id}/publish", new { revision = Revision(draft), note = "模拟历史写入失败" });
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            await using var verify = await Factory.CreateDbContextAsync();
            var row = await verify.HmiLayouts.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(Revision(draft), row.Revision);
            Assert.Null(row.PublishedJson);
            Assert.False(await verify.HmiLayoutVersions.AnyAsync(x => x.LayoutId == id));
            Assert.Equal(logCount, await verify.OperationLogs.CountAsync());
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER FailLayoutPublish;"); }
    }

    [Fact]
    public async Task OldSchemaMigrationPublishesExistingLayoutsAndSeedsHistoryWithCascade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options;
        var json = JsonSerializer.Serialize(Document(Region()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using (var old = new ConfigDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync("20261028055543_AddTraceabilityOutcomes");
            await old.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO HmiLayout (Name, DocumentJson, Revision, UpdatedTs, UpdatedBy) VALUES ({"旧运行布局"}, {json}, {7}, {123L}, {"old-admin"})");
            await old.Database.ExecuteSqlRawAsync("INSERT INTO HmiWorkstation (Name, LayoutId, AllowControl, UpdatedTs, UpdatedBy) VALUES ('legacy-station', 1, 1, 123, 'old-admin')");
        }
        await using var db = new ConfigDbContext(options); await db.Database.MigrateAsync();
        var row = await db.HmiLayouts.SingleAsync(); var history = await db.HmiLayoutVersions.SingleAsync();
        Assert.Equal(row.Name, row.PublishedName); Assert.Equal(json, row.PublishedJson);
        Assert.Equal(7, row.PublishedRevision); Assert.Equal(123, row.PublishedTs); Assert.Equal("old-admin", row.PublishedBy);
        Assert.Equal(7, history.Revision); Assert.Equal(json, history.DocumentJson); Assert.Equal("migration", history.Kind);
        Assert.True(db.Model.FindEntityType(typeof(HmiLayout))!.FindProperty(nameof(HmiLayout.Revision))!.IsConcurrencyToken);
        await using var stale = new ConfigDbContext(options); var staleRow = await stale.HmiLayouts.SingleAsync();
        row.Revision++; await db.SaveChangesAsync(); staleRow.Revision++; staleRow.Name = "旧窗口";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        db.HmiLayouts.Remove(row); await db.SaveChangesAsync();
        Assert.Empty(await db.HmiLayoutVersions.ToListAsync());
        Assert.Null((await db.HmiWorkstations.SingleAsync()).LayoutId);
    }
}
