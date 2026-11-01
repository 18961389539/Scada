using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using static Scada.Server.Tests.McpApiTests;

namespace Scada.Server.Tests;

public sealed class ScreenVersionMcpTests(McpConfigurationTests.Site site) : IClassFixture<McpConfigurationTests.Site>
{
    private IDbContextFactory<ConfigDbContext> Db => site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static string Key() => "mcphistory" + Guid.NewGuid().ToString("N")[..10];
    private static ScreenDocument Document(string text) => new() { Nodes = [new() { Id = "title", Type = "text", Text = text }] };
    private static JsonElement Data(JsonElement result) => McpApiTests.Data(result);

    private async Task<(ProcessScreen Screen, ProcessScreenVersion[] Versions)> StateAsync(int id)
    {
        await using var db = await Db.CreateDbContextAsync();
        return (await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == id),
            await db.ProcessScreenVersions.AsNoTracking().Where(x => x.ScreenId == id).OrderBy(x => x.Revision).ToArrayAsync());
    }

    private static string Bytes((ProcessScreen Screen, ProcessScreenVersion[] Versions) state) =>
        JsonSerializer.Serialize(new { state.Screen, state.Versions });

    private static void AssertPublished(JsonElement result, int id)
    {
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(id, result.GetProperty("screenId").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("message").GetString()));
        Assert.Equal(result.GetProperty("revision").GetInt32(), result.GetProperty("publishedRevision").GetInt32());
        Assert.Empty(result.GetProperty("issues").EnumerateArray());
        Assert.True(result.GetProperty("dataAsOf").GetInt64() > 0);
    }

    [Fact]
    public async Task PublishToolKeepsExistingResultFieldsAndAddsOptionalHistoryArguments()
    {
        using var client = site.Server.CreateClient();
        var tools = (await RpcAsync(client, "tools/list")).GetProperty("tools").EnumerateArray();
        var tool = tools.Single(item => item.GetProperty("name").GetString() == "publish_screen");
        var input = tool.GetProperty("inputSchema");
        var properties = input.GetProperty("properties");
        Assert.True(properties.TryGetProperty("rollbackToRevision", out _));
        Assert.True(properties.TryGetProperty("note", out _));
        if (input.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain(required.EnumerateArray(), item => item.GetString() is "rollbackToRevision" or "note");
        }
        var output = tool.GetProperty("outputSchema").GetProperty("properties");
        foreach (var field in new[] { "screenId", "success", "message", "revision", "publishedRevision", "configVersionId", "warning", "issues", "dataAsOf" })
            Assert.True(output.TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task PublishAndRollbackAppendHistoryAsMcpWhileKeepingTheUnpublishedDraft()
    {
        using var client = site.Server.CreateClient();
        var name = Key();
        var created = Data(await CallAsync(client, "create_screen", new { name }));
        var screenId = created.GetProperty("screenId").GetInt32();
        var saved = Data(await CallAsync(client, "save_screen_draft", new { screenId, revision = 1, name, document = Document("首版") }));
        Assert.True(saved.GetProperty("success").GetBoolean());
        var first = Data(await CallAsync(client, "publish_screen", new { screenId, revision = saved.GetProperty("revision").GetInt32() }));
        AssertPublished(first, screenId);
        var firstRevision = first.GetProperty("publishedRevision").GetInt32();
        var original = Assert.Single((await StateAsync(screenId)).Versions);
        Assert.Equal("publish", original.Kind);
        Assert.Equal("", original.Note);
        Assert.Equal("mcp:lan", original.PublishedBy);
        Assert.Null(original.RestoredFromRevision);

        saved = Data(await CallAsync(client, "save_screen_draft", new { screenId, revision = firstRevision, name = name + "second", document = Document("第二版") }));
        var second = Data(await CallAsync(client, "publish_screen", new { screenId, revision = saved.GetProperty("revision").GetInt32(), note = "  第二次发布  " }));
        AssertPublished(second, screenId);
        saved = Data(await CallAsync(client, "save_screen_draft", new { screenId, revision = second.GetProperty("revision").GetInt32(), name = name + "draft", document = Document("需要保留的草稿") }));
        Assert.True(saved.GetProperty("success").GetBoolean());
        var draft = await StateAsync(screenId);
        Assert.Equal(2, draft.Versions.Length);
        Assert.Equal("第二次发布", draft.Versions[1].Note);
        var revision = saved.GetProperty("revision").GetInt32();
        var rollback = Data(await CallAsync(client, "publish_screen", new { screenId, revision, rollbackToRevision = firstRevision, note = "  恢复首版  " }));
        AssertPublished(rollback, screenId);
        Assert.Equal(revision + 1, rollback.GetProperty("revision").GetInt32());
        var restored = await StateAsync(screenId);
        Assert.Equal(draft.Screen.Name, restored.Screen.Name);
        Assert.Equal(draft.Screen.DraftJson, restored.Screen.DraftJson);
        Assert.Equal(original.Name, restored.Screen.PublishedName);
        Assert.Equal(original.DocumentJson, restored.Screen.PublishedJson);
        Assert.Equal("mcp:lan", restored.Screen.UpdatedBy);
        Assert.Equal(3, restored.Versions.Length);
        Assert.Equal(JsonSerializer.Serialize(draft.Versions), JsonSerializer.Serialize(restored.Versions.Take(2).ToArray()));
        var version = restored.Versions[^1];
        Assert.Equal("rollback", version.Kind);
        Assert.Equal(firstRevision, version.RestoredFromRevision);
        Assert.Equal("恢复首版", version.Note);
        Assert.Equal("mcp:lan", version.PublishedBy);
        Assert.Equal(restored.Screen.PublishedRevision, version.Revision);
        Assert.Equal(original.DocumentJson, version.DocumentJson);

        var before = Bytes(restored);
        var stale = Data(await CallAsync(client, "publish_screen", new { screenId, revision, rollbackToRevision = firstRevision, note = "过期请求" }));
        Assert.False(stale.GetProperty("success").GetBoolean());
        Assert.Equal(restored.Screen.Revision, stale.GetProperty("revision").GetInt32());
        Assert.Equal(before, Bytes(await StateAsync(screenId)));
        foreach (var note in new string?[] { null, "", " \t ", new string('n', 501) })
        {
            var rejected = await CallAsync(client, "publish_screen", new { screenId, revision = restored.Screen.Revision, rollbackToRevision = firstRevision, note });
            Assert.True(rejected.GetProperty("isError").GetBoolean(), rejected.ToString());
            Assert.Equal("invalid_argument", rejected.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(before, Bytes(await StateAsync(screenId)));
        }
        var missing = await CallAsync(client, "publish_screen", new { screenId, revision = restored.Screen.Revision, rollbackToRevision = int.MaxValue, note = "不存在的历史" });
        Assert.True(missing.GetProperty("isError").GetBoolean(), missing.ToString());
        Assert.Equal("not_found", missing.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before, Bytes(await StateAsync(screenId)));
    }

    [Fact]
    public async Task McpRollbackRechecksTheFrozenCommandAgainstCurrentWritableTags()
    {
        using var admin = await site.ClientAsync("admin");
        using var client = site.Server.CreateClient();
        var name = Key();
        using var response = await admin.PostAsJsonAsync("/api/tags", site.TagBody(site.DeviceA, name));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var tag = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var screenId = Data(await CallAsync(client, "create_screen", new { name })).GetProperty("screenId").GetInt32();
        var document = new ScreenDocument { Nodes = [new() { Id = "command", Type = "button", Action = new() { Type = "command", TagId = tag, Value = 1 } }] };
        var saved = Data(await CallAsync(client, "save_screen_draft", new { screenId, revision = 1, name, document }));
        Assert.True(saved.GetProperty("success").GetBoolean());
        var first = Data(await CallAsync(client, "publish_screen", new { screenId, revision = saved.GetProperty("revision").GetInt32() }));
        AssertPublished(first, screenId);
        var target = first.GetProperty("publishedRevision").GetInt32();
        saved = Data(await CallAsync(client, "save_screen_draft", new { screenId, revision = target, name, document = Document("新的运行画面") }));
        var current = Data(await CallAsync(client, "publish_screen", new { screenId, revision = saved.GetProperty("revision").GetInt32() }));
        AssertPublished(current, screenId);
        await using (var db = await Db.CreateDbContextAsync())
            Assert.Equal(1, await db.Tags.Where(x => x.Id == tag).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Writable, false)));
        var before = Bytes(await StateAsync(screenId));
        var refused = Data(await CallAsync(client, "publish_screen", new { screenId, revision = current.GetProperty("revision").GetInt32(), rollbackToRevision = target, note = "不可写目标" }));
        Assert.False(refused.GetProperty("success").GetBoolean(), refused.ToString());
        Assert.Contains(refused.GetProperty("issues").EnumerateArray(), issue => issue.GetString()!.Contains("可写"));
        Assert.Equal(before, Bytes(await StateAsync(screenId)));
    }
}
