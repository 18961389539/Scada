using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class ScreenVersionHistoryTests(ControlTests.ControlSite site) : IClassFixture<ControlTests.ControlSite>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IDbContextFactory<ConfigDbContext> Db => site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static string Key() => "history" + Guid.NewGuid().ToString("N")[..10];
    private static ScreenDocument Text(string text) => new() { Nodes = [new() { Id = "title", Type = "text", Text = text }] };

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    private static async Task<int> CreateAsync(HttpClient client, string name) =>
        (await Ok(await client.PostAsJsonAsync("/api/screens", new { name }))).GetProperty("id").GetInt32();

    private static async Task<int> SaveAsync(HttpClient client, int id, int revision, string name, ScreenDocument document) =>
        (await Ok(await client.PutAsJsonAsync($"/api/screens/{id}", new { name, revision, document }))).GetProperty("revision").GetInt32();

    private static async Task<int> PublishAsync(HttpClient client, int id, int revision, string? note = null) =>
        (await Ok(await client.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision, note }))).GetProperty("publishedRevision").GetInt32();

    private async Task<ProcessScreen> ScreenAsync(int id)
    {
        await using var db = await Db.CreateDbContextAsync();
        return await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<ProcessScreenVersion[]> HistoryAsync(int id)
    {
        await using var db = await Db.CreateDbContextAsync();
        return await db.ProcessScreenVersions.AsNoTracking().Where(x => x.ScreenId == id).OrderBy(x => x.Revision).ToArrayAsync();
    }

    private async Task<string> StateAsync(int id)
    {
        await using var db = await Db.CreateDbContextAsync();
        var screen = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == id);
        return JsonSerializer.Serialize(new
        {
            screen,
            versions = await db.ProcessScreenVersions.AsNoTracking().Where(x => x.ScreenId == id).OrderBy(x => x.Revision).ToArrayAsync(),
            successes = await db.OperationLogs.AsNoTracking()
                .Where(x => x.Result == "成功" && x.Action.Contains(screen.Name)
                    && (x.Action.StartsWith("发布画面") || x.Action.StartsWith("回退画面")))
                .OrderBy(x => x.Id).Select(x => new { x.Id, x.Action, x.Result }).ToArrayAsync()
        });
    }

    [Fact]
    public async Task RollbackAppendsFrozenPublicationAndPreservesTheNewerDraftAndAllEarlierJson()
    {
        using var admin = await site.ClientAsync("admin");
        using var op = await site.ClientAsync("opA");
        var key = Key();
        var templateKey = key + "template";
        var symbolKey = key + "symbol";
        var symbol = "user:" + symbolKey;
        const string oldSvg = "<svg viewBox=\"0 0 100 100\"><rect width=\"80\" height=\"80\" fill=\"#123456\"/></svg>";
        const string newSvg = "<svg viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"#abcdef\"/></svg>";
        const string latestSvg = "<svg viewBox=\"0 0 100 100\"><path d=\"M0 0 L80 80\" stroke=\"#ffffff\"/></svg>";
        var firstTag = (await Ok(await admin.PostAsJsonAsync("/api/tags", site.TagBody(site.DeviceA, key + "first")))).GetProperty("id").GetInt32();
        var secondTag = (await Ok(await admin.PostAsJsonAsync("/api/tags", site.TagBody(site.DeviceA, key + "second")))).GetProperty("id").GetInt32();
        var cache = site.Services.GetRequiredService<IRealtimeCache>();
        cache.Set(new TagValue(firstTag, 0, TagQuality.Good, ScadaTime.NowMs()));
        cache.Set(new TagValue(secondTag, 0, TagQuality.Good, ScadaTime.NowMs()));
        var firstTarget = await CreateAsync(admin, key + "target1");
        var secondTarget = await CreateAsync(admin, key + "target2");
        await PublishAsync(admin, firstTarget, 1);
        await PublishAsync(admin, secondTarget, 1);

        object Symbol(string svg, int revision = 0) => new { key = symbolKey, name = symbolKey, kind = "symbol", width = 100, height = 100, svg, revision };
        object Template(string bind, double value, int revision = 0) => new
        {
            key = templateKey, name = templateKey, kind = "template", width = 600, height = 240,
            placeholders = new[] { "first", "second" }, revision,
            document = new ScreenDocument
            {
                Width = 600, Height = 240,
                Nodes = [
                    new() { Id = "symbol", Type = "symbol", Width = 100, Height = 100, Symbol = symbol },
                    new() { Id = "start", Type = "button", X = 120, Width = 100, Height = 40,
                        Action = new() { Type = "command", Bind = bind, Value = value } },
                    new() { Id = "panel", Type = "equipment-panel", X = 240, Width = 320, Height = 240,
                        Commands = [new() { Key = "start", Label = "启动", Bind = bind, Value = value }] }
                ]
            }
        };
        var symbolItem = (await Ok(await admin.PostAsJsonAsync("/api/hmi/library", Symbol(oldSvg)))).GetProperty("item");
        var templateItem = (await Ok(await admin.PostAsJsonAsync("/api/hmi/library", Template("first", 11)))).GetProperty("item");
        var document = new ScreenDocument
        {
            Width = 800, Height = 480,
            Nodes = [
                new() { Id = "instance", Type = "template", TemplateKey = templateKey, Width = 600, Height = 240,
                    Bindings = [new() { Key = "first", TagId = firstTag }, new() { Key = "second", TagId = secondTag }] },
                new() { Id = "next", Type = "button", Y = 300, Action = new() { Type = "navigate", ScreenId = firstTarget } }
            ]
        };
        var id = await CreateAsync(admin, key + "first");
        var revision = await SaveAsync(admin, id, 1, key + "first", document);
        Assert.Empty(await HistoryAsync(id));
        var firstRevision = await PublishAsync(admin, id, revision, "  首次发布  ");
        var first = Assert.Single(await HistoryAsync(id));
        Assert.Equal(firstRevision, first.Revision);
        Assert.Equal("首次发布", first.Note);
        Assert.Equal("publish", first.Kind);
        Assert.Equal("admin", first.PublishedBy);
        Assert.True(first.PublishedTs > 0);
        Assert.Null(first.RestoredFromRevision);

        void AssertFrozen(string json, int tag, double value, int target, string svg)
        {
            var frozen = JsonSerializer.Deserialize<ScreenDocument>(json, Json)!;
            Assert.DoesNotContain(frozen.Nodes, node => node.Type == "template");
            Assert.All(frozen.Nodes, node => { Assert.Null(node.TemplateKey); Assert.Null(node.Bindings); Assert.Null(node.Action.Bind); });
            var action = frozen.Nodes.Single(node => node.Id == "instance--start").Action;
            Assert.Equal(tag, action.TagId);
            Assert.Equal(value, action.Value);
            var command = Assert.Single(frozen.Nodes.Single(node => node.Id == "instance--panel").Commands!);
            Assert.Equal(tag, command.TagId);
            Assert.Equal(value, command.Value);
            Assert.Null(command.Bind);
            Assert.Equal(target, frozen.Nodes.Single(node => node.Id == "next").Action.ScreenId);
            Assert.Equal(svg, Assert.Single(frozen.SymbolAssets!).Value);
            Assert.Equal(symbol, Assert.Single(frozen.SymbolAssets!).Key);
        }
        AssertFrozen(first.DocumentJson, firstTag, 11, firstTarget, oldSvg);
        templateItem = (await Ok(await admin.PutAsJsonAsync($"/api/hmi/library/{templateItem.GetProperty("id").GetInt32()}", Template("second", 22, templateItem.GetProperty("revision").GetInt32())))).GetProperty("item");
        symbolItem = (await Ok(await admin.PutAsJsonAsync($"/api/hmi/library/{symbolItem.GetProperty("id").GetInt32()}", Symbol(newSvg, symbolItem.GetProperty("revision").GetInt32())))).GetProperty("item");
        document.Nodes.Single(node => node.Id == "next").Action.ScreenId = secondTarget;
        revision = await SaveAsync(admin, id, firstRevision, key + "second", document);
        var secondRevision = await PublishAsync(admin, id, revision);
        var priorHistory = await HistoryAsync(id);
        Assert.Equal(2, priorHistory.Length);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(priorHistory[0]));
        AssertFrozen(priorHistory[1].DocumentJson, secondTag, 22, secondTarget, newSvg);

        await Ok(await admin.PutAsJsonAsync($"/api/hmi/library/{templateItem.GetProperty("id").GetInt32()}", Template("second", 33, templateItem.GetProperty("revision").GetInt32())));
        await Ok(await admin.PutAsJsonAsync($"/api/hmi/library/{symbolItem.GetProperty("id").GetInt32()}", Symbol(latestSvg, symbolItem.GetProperty("revision").GetInt32())));
        revision = await SaveAsync(admin, id, secondRevision, key + "pending", Text("尚未发布的编辑"));
        var draft = await ScreenAsync(id);
        Assert.Equal(JsonSerializer.Serialize(priorHistory), JsonSerializer.Serialize(await HistoryAsync(id)));
        var rollback = await Ok(await admin.PostAsJsonAsync($"/api/screens/{id}/rollback", new { revision, targetRevision = firstRevision, note = "  恢复已验证版本  " }));
        var restoredRevision = rollback.GetProperty("publishedRevision").GetInt32();
        Assert.Equal(revision + 1, restoredRevision);
        Assert.Equal(restoredRevision, rollback.GetProperty("revision").GetInt32());
        var restored = await ScreenAsync(id);
        Assert.Equal(draft.Name, restored.Name);
        Assert.Equal(draft.DraftJson, restored.DraftJson);
        Assert.Equal(first.Name, restored.PublishedName);
        Assert.Equal(first.DocumentJson, restored.PublishedJson);
        Assert.Equal(restoredRevision, restored.Revision);
        Assert.Equal(restoredRevision, restored.PublishedRevision);
        var history = await HistoryAsync(id);
        Assert.Equal(3, history.Length);
        Assert.Equal(JsonSerializer.Serialize(priorHistory), JsonSerializer.Serialize(history.Take(2).ToArray()));
        Assert.Equal("rollback", history[2].Kind);
        Assert.Equal(firstRevision, history[2].RestoredFromRevision);
        Assert.Equal(restoredRevision, history[2].Revision);
        Assert.Equal("恢复已验证版本", history[2].Note);
        Assert.Equal("admin", history[2].PublishedBy);
        Assert.Equal(first.DocumentJson, history[2].DocumentJson);
        AssertFrozen(restored.PublishedJson!, firstTag, 11, firstTarget, oldSvg);
        var detail = await Ok(await admin.GetAsync($"/api/screens/{id}/versions/{firstRevision}"));
        Assert.Equal(firstRevision, detail.GetProperty("revision").GetInt32());
        Assert.Equal(first.Name, detail.GetProperty("name").GetString());
        Assert.Equal(first.PublishedTs, detail.GetProperty("publishedTs").GetInt64());
        Assert.Equal(first.PublishedBy, detail.GetProperty("publishedBy").GetString());
        Assert.Equal(first.Note, detail.GetProperty("note").GetString());
        Assert.Equal(first.Kind, detail.GetProperty("kind").GetString());
        Assert.False(detail.GetProperty("requiresRepublish").GetBoolean());
        AssertFrozen(detail.GetProperty("document").GetRawText(), firstTag, 11, firstTarget, oldSvg);
        var running = await Ok(await op.GetAsync($"/api/screens/{id}"));
        Assert.Equal(first.Name, running.GetProperty("name").GetString());
        Assert.Equal(restoredRevision, running.GetProperty("publishedRevision").GetInt32());
        var catalog = await Ok(await admin.GetAsync("/api/screens?draft=true"));
        Assert.True(catalog.EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == id).GetProperty("hasDraftChanges").GetBoolean());

        var collector = site.Services.GetRequiredService<ControlTests.FakeCollector>();
        var firstWrites = collector.Count(firstTag);
        var secondWrites = collector.Count(secondTag);
        foreach (var oldRevision in new[] { firstRevision, secondRevision })
        {
            using var refused = await op.PostAsJsonAsync($"/api/screens/{id}/actions/instance--start", new { publishedRevision = oldRevision, confirmed = true, requestId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }
        using (var changedValue = await op.PostAsJsonAsync($"/api/screens/{id}/actions/instance--start", new { publishedRevision = restoredRevision, value = 22, confirmed = true, requestId = Guid.NewGuid() }))
            Assert.Equal(HttpStatusCode.BadRequest, changedValue.StatusCode);
        Assert.Equal(firstWrites, collector.Count(firstTag));
        var button = await Ok(await op.PostAsJsonAsync($"/api/screens/{id}/actions/instance--start", new { publishedRevision = restoredRevision, confirmed = true, requestId = Guid.NewGuid() }));
        Assert.True(button.GetProperty("success").GetBoolean(), button.ToString());
        var panel = await Ok(await op.PostAsJsonAsync($"/api/screens/{id}/actions/instance--panel", new { publishedRevision = restoredRevision, operation = "start", confirmed = true, requestId = Guid.NewGuid() }));
        Assert.True(panel.GetProperty("success").GetBoolean(), panel.ToString());
        Assert.Equal(firstWrites + 2, collector.Count(firstTag));
        Assert.Equal(secondWrites, collector.Count(secondTag));
        lock (collector.Writes)
            Assert.All(collector.Writes.Where(write => write.TagId == firstTag), write => Assert.Equal(11d, write.Value));
        await PublishAsync(admin, id, restoredRevision);
        catalog = await Ok(await admin.GetAsync("/api/screens?draft=true"));
        Assert.False(catalog.EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == id).GetProperty("hasDraftChanges").GetBoolean());
    }

    [Fact]
    public async Task PublicationRetainsOnlyTheNewestVersionsPerScreen()
    {
        using var admin = await site.ClientAsync("admin");
        var name = Key();
        var id = await CreateAsync(admin, name);
        await using (var db = await Db.CreateDbContextAsync())
        {
            var screen = await db.ProcessScreens.SingleAsync(x => x.Id == id);
            screen.PublishedName = name;
            screen.PublishedJson = screen.DraftJson;
            screen.Revision = 120;
            screen.PublishedRevision = 120;
            db.ProcessScreenVersions.AddRange(Enumerable.Range(1, 120).Select(revision => new ProcessScreenVersion
            {
                ScreenId = id, Revision = revision, Name = name, DocumentJson = screen.DraftJson,
                PublishedTs = revision, PublishedBy = "seed", Kind = "publish"
            }));
            await db.SaveChangesAsync();
        }
        var revision = await SaveAsync(admin, id, 120, name, Text("待发布"));
        var published = await PublishAsync(admin, id, revision);
        var history = await HistoryAsync(id);
        Assert.Equal(ProcessScreenHistory.RetainedVersions, history.Length);
        Assert.Equal(published, history[^1].Revision);
        Assert.Equal(22, history[0].Revision);
        Assert.All(history, version => Assert.True(version.Revision == published || version.Revision <= 120));
    }

    [Theory]
    [InlineData("opA")]
    [InlineData("cfgA")]
    public async Task HistoryAndRollbackRequireGlobalConfigurationButRuntimeRemainsReadable(string user)
    {
        using var admin = await site.ClientAsync("admin");
        var id = await CreateAsync(admin, Key());
        var revision = await PublishAsync(admin, id, 1);
        var before = await StateAsync(id);
        using var client = await site.ClientAsync(user);
        await Ok(await client.GetAsync("/api/screens"));
        await Ok(await client.GetAsync($"/api/screens/{id}"));
        using var list = await client.GetAsync($"/api/screens/{id}/versions");
        using var detail = await client.GetAsync($"/api/screens/{id}/versions/{revision}");
        using var publish = await client.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision });
        using var rollback = await client.PostAsJsonAsync($"/api/screens/{id}/rollback", new { revision, targetRevision = revision, note = "无全局权限" });
        Assert.All(new[] { list, detail, publish, rollback }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        using var anonymous = site.CreateClient();
        using var anonymousList = await anonymous.GetAsync($"/api/screens/{id}/versions");
        using var anonymousDetail = await anonymous.GetAsync($"/api/screens/{id}/versions/{revision}");
        using var anonymousRollback = await anonymous.PostAsJsonAsync($"/api/screens/{id}/rollback", new { revision, targetRevision = revision, note = "未登录" });
        Assert.All(new[] { anonymousList, anonymousDetail, anonymousRollback }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(before, await StateAsync(id));
    }

    [Fact]
    public async Task NotesAreTrimmedAndInvalidOrStaleRequestsCannotChangePublicationOrHistory()
    {
        using var admin = await site.ClientAsync("admin");
        var name = Key();
        var id = await CreateAsync(admin, name);
        var revision = await SaveAsync(admin, id, 1, name, Text("草稿"));
        var initial = await StateAsync(id);
        using (var tooLong = await admin.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision, note = new string('n', 501) }))
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(initial, await StateAsync(id));
        var target = await PublishAsync(admin, id, revision, "  " + new string('n', 500) + "  ");
        Assert.Equal(new string('n', 500), Assert.Single(await HistoryAsync(id)).Note);
        revision = await PublishAsync(admin, id, target);
        Assert.Equal("", (await HistoryAsync(id))[^1].Note);
        revision = await SaveAsync(admin, id, revision, name, Text("保留的草稿"));
        var before = await StateAsync(id);
        foreach (var note in new string?[] { null, "", " \t\r\n ", new string('n', 501) })
        {
            using var invalid = await admin.PostAsJsonAsync($"/api/screens/{id}/rollback", new { revision, targetRevision = target, note });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal(before, await StateAsync(id));
        }
        using var stalePublish = await admin.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision = revision - 1 });
        using var staleRollback = await admin.PostAsJsonAsync($"/api/screens/{id}/rollback", new { revision = revision - 1, targetRevision = target, note = "过期" });
        using var missing = await admin.PostAsJsonAsync($"/api/screens/{id}/rollback", new { revision, targetRevision = int.MaxValue, note = "不存在" });
        Assert.Equal(HttpStatusCode.Conflict, stalePublish.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, staleRollback.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(before, await StateAsync(id));
    }

    [Fact]
    public async Task HistoryUsesDescendingExclusiveRevisionPagingAndClampsTheRequestedLimit()
    {
        using var admin = await site.ClientAsync("admin");
        var name = Key();
        var id = await CreateAsync(admin, name);
        await using (var db = await Db.CreateDbContextAsync())
        {
            var screen = await db.ProcessScreens.SingleAsync(x => x.Id == id);
            screen.PublishedName = name;
            screen.PublishedJson = screen.DraftJson;
            screen.Revision = 205;
            screen.PublishedRevision = 205;
            db.ProcessScreenVersions.AddRange(Enumerable.Range(1, 205).Select(revision => new ProcessScreenVersion
            {
                ScreenId = id, Revision = revision, Name = name, DocumentJson = screen.DraftJson,
                PublishedTs = revision, PublishedBy = "seed", Kind = "publish"
            }));
            await db.SaveChangesAsync();
        }
        async Task<int[]> Page(string query = "") => (await Ok(await admin.GetAsync($"/api/screens/{id}/versions{query}")))
            .EnumerateArray().Select(version => version.GetProperty("revision").GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(156, 50).Reverse(), await Page());
        Assert.Equal(new[] { 203, 202 }, await Page("?limit=2&beforeRevision=204"));
        Assert.Equal(new[] { 205, 204 }, await Page("?limit=2&beforeRevision=999"));
        Assert.Empty(await Page("?beforeRevision=1"));
        Assert.Equal(new[] { 205 }, await Page("?limit=0"));
        Assert.Equal(new[] { 205 }, await Page("?limit=-10"));
        Assert.Equal(Enumerable.Range(6, 200).Reverse(), await Page("?limit=999"));
        foreach (var invalid in new[] { 0, -1 })
        {
            using var response = await admin.GetAsync($"/api/screens/{id}/versions?beforeRevision={invalid}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var missingVersion = await admin.GetAsync($"/api/screens/{id}/versions/9999");
        using var missingScreen = await admin.GetAsync($"/api/screens/{int.MaxValue}/versions");
        Assert.Equal(HttpStatusCode.NotFound, missingVersion.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingScreen.StatusCode);
    }

    [Theory]
    [InlineData("template")]
    [InlineData("symbol")]
    public async Task LegacyHistoryIsInspectableButCannotBeRolledBackWithoutRepublishing(string kind)
    {
        using var admin = await site.ClientAsync("admin");
        var name = Key();
        var id = await CreateAsync(admin, name);
        var revision = await PublishAsync(admin, id, 1);
        var legacy = new ScreenDocument
        {
            Nodes = [new() { Id = "legacy", Type = kind, TemplateKey = kind == "template" ? "legacy" : null,
                Symbol = kind == "symbol" ? "user:legacy" : null }]
        };
        await using (var db = await Db.CreateDbContextAsync())
        {
            db.ProcessScreenVersions.Add(new ProcessScreenVersion
            {
                ScreenId = id, Revision = 1, Name = name, DocumentJson = JsonSerializer.Serialize(legacy, Json),
                Kind = "migration", Note = "补录旧发布", PublishedBy = "legacy", PublishedTs = 1000
            });
            await db.SaveChangesAsync();
        }
        var detail = await Ok(await admin.GetAsync($"/api/screens/{id}/versions/1"));
        Assert.True(detail.GetProperty("requiresRepublish").GetBoolean());
        Assert.Equal(kind, Assert.Single(detail.GetProperty("document").GetProperty("nodes").EnumerateArray()).GetProperty("type").GetString());
        var before = await StateAsync(id);
        using var rejected = await admin.PostAsJsonAsync($"/api/screens/{id}/rollback", new { revision, targetRevision = 1, note = "尝试旧版本" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var refusal = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("重新发布", refusal.GetProperty("message").GetString());
        Assert.Equal(before, await StateAsync(id));
    }

    [Theory]
    [InlineData("deleted-navigation")]
    [InlineData("deleted-tag")]
    [InlineData("readonly-command")]
    [InlineData("out-of-range-command")]
    [InlineData("malicious-svg")]
    public async Task RollbackRevalidatesHistoricalReferencesAndAssetsWithoutLeavingSuccessfulRecords(string failure)
    {
        using var admin = await site.ClientAsync("admin");
        var name = Key();
        var document = Text("旧版本");
        var tag = 0;
        var target = 0;
        if (failure == "deleted-navigation")
        {
            target = await CreateAsync(admin, name + "target");
            await PublishAsync(admin, target, 1);
            document.Nodes[0].Action = new() { Type = "navigate", ScreenId = target };
        }
        else if (failure is "deleted-tag" or "readonly-command" or "out-of-range-command")
        {
            tag = (await Ok(await admin.PostAsJsonAsync("/api/tags", site.TagBody(site.DeviceA, name)))).GetProperty("id").GetInt32();
            document.Nodes[0].Type = "button";
            document.Nodes[0].Action = new() { Type = "command", TagId = tag, Value = 1 };
        }
        else
        {
            await Ok(await admin.PostAsJsonAsync("/api/hmi/library", new { key = name, name, kind = "symbol", width = 100, height = 100,
                svg = "<svg viewBox=\"0 0 100 100\"><rect width=\"80\" height=\"80\"/></svg>" }));
            document.Nodes = [new() { Id = "symbol", Type = "symbol", Symbol = "user:" + name }];
        }
        var id = await CreateAsync(admin, name);
        var revision = await SaveAsync(admin, id, 1, name, document);
        var historicalRevision = await PublishAsync(admin, id, revision);
        revision = await SaveAsync(admin, id, historicalRevision, name, Text("当前安全版本"));
        revision = await PublishAsync(admin, id, revision);
        if (failure == "deleted-navigation")
        {
            using var deleted = await admin.DeleteAsync($"/api/screens/{target}?revision=2");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }
        else
        {
            await using var db = await Db.CreateDbContextAsync();
            if (failure == "deleted-tag")
                Assert.Equal(1, await db.Tags.Where(x => x.Id == tag).ExecuteDeleteAsync());
            else if (failure == "readonly-command")
                Assert.Equal(1, await db.Tags.Where(x => x.Id == tag).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Writable, false)));
            else if (failure == "out-of-range-command")
                Assert.Equal(1, await db.Tags.Where(x => x.Id == tag).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.MaxValue, 0.5)));
            else
            {
                var oldVersion = await db.ProcessScreenVersions.SingleAsync(x => x.ScreenId == id && x.Revision == historicalRevision);
                var frozen = JsonSerializer.Deserialize<ScreenDocument>(oldVersion.DocumentJson, Json)!;
                frozen.SymbolAssets!["user:" + name] = "<svg><script>alert(1)</script></svg>";
                oldVersion.DocumentJson = JsonSerializer.Serialize(frozen, Json);
                await db.SaveChangesAsync();
            }
        }
        var before = await StateAsync(id);
        using var response = await admin.PostAsJsonAsync($"/api/screens/{id}/rollback", new { revision, targetRevision = historicalRevision, note = "应重新校验" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(body.GetProperty("issues").EnumerateArray());
        var issues = string.Join("; ", body.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("message").GetString()));
        var expected = failure switch
        {
            "deleted-navigation" => "目标画面",
            "deleted-tag" => "测点",
            "readonly-command" => "写",
            "out-of-range-command" => "范围",
            _ => "script"
        };
        Assert.Contains(expected, issues, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await StateAsync(id));
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("rollback")]
    public async Task ConcurrentDuplicateRevisionCanSucceedOnlyOnce(string operation)
    {
        using var admin = await site.ClientAsync("admin");
        var name = Key();
        var id = await CreateAsync(admin, name);
        var target = await PublishAsync(admin, id, 1);
        var revision = await SaveAsync(admin, id, target, name, Text("新草稿"));
        var originalHistory = await HistoryAsync(id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => admin.PostAsJsonAsync($"/api/screens/{id}/{operation}",
            new { revision, targetRevision = target, note = "重复请求" })));
        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            var row = await ScreenAsync(id);
            Assert.Equal(revision + 1, row.Revision);
            Assert.Equal(row.Revision, row.PublishedRevision);
            var versions = await HistoryAsync(id);
            Assert.Equal(originalHistory.Length + 1, versions.Length);
            Assert.Equal(JsonSerializer.Serialize(originalHistory), JsonSerializer.Serialize(versions.Take(originalHistory.Length).ToArray()));
            Assert.Equal(operation, versions[^1].Kind);
            Assert.Equal(row.PublishedJson, versions[^1].DocumentJson);
            Assert.Equal(operation == "rollback" ? (int?)target : null, versions[^1].RestoredFromRevision);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task ProjectImportAndProjectRollbackAppendLocalHistoryThatSurvivesRestartWithoutCopyingSourceHistory()
    {
        using var admin = await site.ClientAsync("admin");
        var name = Key();
        var id = await CreateAsync(admin, name);
        var revision = await SaveAsync(admin, id, 1, name, Text("源环境最初版本"));
        revision = await PublishAsync(admin, id, revision);
        revision = await SaveAsync(admin, id, revision, name, Text("导出的当前发布"));
        await PublishAsync(admin, id, revision);
        var sourceHistory = await HistoryAsync(id);
        Assert.Equal(2, sourceHistory.Length);
        var package = JsonNode.Parse((await Ok(await admin.GetAsync("/api/project/export"))).GetRawText())!.AsObject();
        var sections = package["sections"]!.AsObject();
        var exportedScreen = sections["screens"]!.AsArray().OfType<JsonObject>().Single(item => item["key"]!.GetValue<string>() == name).DeepClone();
        foreach (var section in sections.Select(pair => pair.Key).ToArray()) sections[section] = new JsonArray();
        sections["screens"]!.AsArray().Add(exportedScreen);

        var target = new AuthorizationTests.Site();
        await target.InitializeAsync();
        try
        {
            using var client = await target.ClientAsync("admin");
            var preview = await Ok(await client.PostAsJsonAsync("/api/project/import/preview", new { package, resolution = "Skip" }));
            Assert.Empty(preview.GetProperty("errors").EnumerateArray());
            await Ok(await client.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() }));
            var targetDb = target.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
            ProcessScreen imported;
            ProcessScreenVersion importedVersion;
            await using (var db = await targetDb.CreateDbContextAsync())
            {
                imported = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Name == name);
                importedVersion = Assert.Single(await db.ProcessScreenVersions.AsNoTracking().Where(x => x.ScreenId == imported.Id).ToArrayAsync());
                Assert.Equal("import", importedVersion.Kind);
                Assert.Equal(imported.PublishedRevision, importedVersion.Revision);
                Assert.Equal(imported.PublishedJson, importedVersion.DocumentJson);
                Assert.Equal(sourceHistory[^1].DocumentJson, importedVersion.DocumentJson);
                Assert.Null(importedVersion.RestoredFromRevision);
            }
            var snapshot = await Ok(await client.PostAsJsonAsync("/api/project/versions", new { note = "记录导入的发布" }));
            var snapshotId = snapshot.GetProperty("id").GetInt32();
            var changed = await SaveAsync(client, imported.Id, imported.Revision, name, Text("目标环境后续发布"));
            var latestRevision = await PublishAsync(client, imported.Id, changed);
            ProcessScreenVersion[] beforeRollback;
            await using (var db = await targetDb.CreateDbContextAsync())
                beforeRollback = await db.ProcessScreenVersions.AsNoTracking().Where(x => x.ScreenId == imported.Id).OrderBy(x => x.Revision).ToArrayAsync();
            Assert.Equal(2, beforeRollback.Length);
            var rollback = await Ok(await client.PostAsync($"/api/project/versions/{snapshotId}/rollback/preview", null));
            Assert.Empty(rollback.GetProperty("errors").EnumerateArray());
            await Ok(await client.PostAsJsonAsync($"/api/project/versions/{snapshotId}/rollback", new { baseSha = rollback.GetProperty("baseSha").GetString() }));
            string persistentState;
            await using (var db = await targetDb.CreateDbContextAsync())
            {
                var restored = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == imported.Id);
                var versions = await db.ProcessScreenVersions.AsNoTracking().Where(x => x.ScreenId == imported.Id).OrderBy(x => x.Revision).ToArrayAsync();
                Assert.True(restored.PublishedRevision > latestRevision);
                Assert.Equal(imported.PublishedJson, restored.PublishedJson);
                Assert.Equal(3, versions.Length);
                Assert.Equal(JsonSerializer.Serialize(beforeRollback), JsonSerializer.Serialize(versions.Take(2).ToArray()));
                Assert.Equal("import", versions[^1].Kind);
                Assert.Equal(restored.PublishedRevision, versions[^1].Revision);
                Assert.Equal(importedVersion.DocumentJson, versions[^1].DocumentJson);
                persistentState = JsonSerializer.Serialize(new { screen = restored, versions });
            }
            await target.RestartAsync();
            await using (var db = await target.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync())
            {
                var screen = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == imported.Id);
                var versions = await db.ProcessScreenVersions.AsNoTracking().Where(x => x.ScreenId == imported.Id).OrderBy(x => x.Revision).ToArrayAsync();
                Assert.Equal(persistentState, JsonSerializer.Serialize(new { screen, versions }));
            }
            using var restarted = await target.ClientAsync("admin");
            var history = await Ok(await restarted.GetAsync($"/api/screens/{imported.Id}/versions"));
            Assert.Equal(3, history.GetArrayLength());
            Assert.Equal("import", history[0].GetProperty("kind").GetString());
            Assert.Equal(JsonSerializer.Serialize(sourceHistory), JsonSerializer.Serialize(await HistoryAsync(id)));
        }
        finally
        {
            await target.DisposeAsync();
        }
    }
}
