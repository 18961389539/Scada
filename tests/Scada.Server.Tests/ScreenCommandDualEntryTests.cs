using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

/// <summary>画面与区域写入：REST 与 MCP 走同一命令服务，交叉区域测点与重名拒绝必须一致。</summary>
public sealed class ScreenCommandDualEntryTests(McpConfigurationTests.Site site) : IClassFixture<McpConfigurationTests.Site>
{
    private static ScreenDocument Text(string text) => new() { Nodes = [new() { Id = "title", Type = "text", Text = text }] };
    private static ScreenDocument Bound(int tagId) => new() { Nodes = [new() { Id = "v", Type = "value", TagId = tagId }] };

    [Fact]
    public async Task CrossAreaTagIsRejectedByBothRestAndMcpOnSaveAndPublish()
    {
        using var rest = await site.ClientAsync("admin");
        using var mcp = site.Server.CreateClient();
        var name = "dual" + Guid.NewGuid().ToString("N")[..8];

        var created = await Ok(await rest.PostAsJsonAsync("/api/screens", new { name, areaId = site.AreaA }));
        var restId = created.GetProperty("id").GetInt32();
        var saved = await rest.PutAsJsonAsync($"/api/screens/{restId}", new { name, revision = 1, document = Bound(site.TagB) });
        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        Assert.Contains("画面绑定了其他区域的测点", await saved.Content.ReadAsStringAsync());

        var mcpCreated = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "create_screen", new { name = name + "mcp", areaId = site.AreaA }));
        Assert.True(mcpCreated.GetProperty("success").GetBoolean(), mcpCreated.ToString());
        var mcpId = mcpCreated.GetProperty("screenId").GetInt32();
        var mcpSaved = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "save_screen_draft", new { screenId = mcpId, revision = 1, name = name + "mcp", document = Bound(site.TagB) }));
        Assert.False(mcpSaved.GetProperty("success").GetBoolean(), mcpSaved.ToString());
        Assert.Contains("画面绑定了其他区域的测点", mcpSaved.GetProperty("message").GetString());

        // 保存已被拦住后，草稿里还没有跨区域测点。直接把非法文档写入草稿，确认发布入口也会拒绝（避免只拦保存、不拦发布）。
        await using var db = await site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        var restScreen = await db.ProcessScreens.SingleAsync(x => x.Id == restId);
        restScreen.DraftJson = JsonSerializer.Serialize(Bound(site.TagB), ScreenCommandServiceJson());
        var mcpScreen = await db.ProcessScreens.SingleAsync(x => x.Id == mcpId);
        mcpScreen.DraftJson = JsonSerializer.Serialize(Bound(site.TagB), ScreenCommandServiceJson());
        await db.SaveChangesAsync();
        var published = await rest.PostAsJsonAsync($"/api/screens/{restId}/publish", new { revision = restScreen.Revision });
        Assert.Equal(HttpStatusCode.BadRequest, published.StatusCode);
        Assert.Contains("画面绑定了其他区域的测点", await published.Content.ReadAsStringAsync());

        var mcpPublished = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "publish_screen", new { screenId = mcpId, revision = mcpScreen.Revision }));
        Assert.False(mcpPublished.GetProperty("success").GetBoolean(), mcpPublished.ToString());
        Assert.Contains("画面绑定了其他区域的测点", mcpPublished.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SamePlainDocumentPublishesThroughBothEntries()
    {
        using var rest = await site.ClientAsync("admin");
        using var mcp = site.Server.CreateClient();
        var restName = "pubr" + Guid.NewGuid().ToString("N")[..8];
        var mcpName = "pubm" + Guid.NewGuid().ToString("N")[..8];

        var created = await Ok(await rest.PostAsJsonAsync("/api/screens", new { name = restName, areaId = site.AreaA }));
        var restId = created.GetProperty("id").GetInt32();
        var saved = await Ok(await rest.PutAsJsonAsync($"/api/screens/{restId}", new { name = restName, revision = 1, document = Text("rest") }));
        var published = await Ok(await rest.PostAsJsonAsync($"/api/screens/{restId}/publish", new { revision = saved.GetProperty("revision").GetInt32() }));
        Assert.True(published.GetProperty("publishedRevision").GetInt32() > 0);

        var mcpCreated = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "create_screen", new { name = mcpName, areaId = site.AreaA }));
        var mcpId = mcpCreated.GetProperty("screenId").GetInt32();
        var mcpSaved = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "save_screen_draft", new { screenId = mcpId, revision = 1, name = mcpName, document = Text("mcp") }));
        Assert.True(mcpSaved.GetProperty("success").GetBoolean(), mcpSaved.ToString());
        var mcpPublished = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "publish_screen", new { screenId = mcpId, revision = mcpSaved.GetProperty("revision").GetInt32() }));
        Assert.True(mcpPublished.GetProperty("success").GetBoolean(), mcpPublished.ToString());
        Assert.True(mcpPublished.GetProperty("publishedRevision").GetInt32() > 0);
        Assert.Empty(mcpPublished.GetProperty("issues").EnumerateArray());
    }

    private static JsonSerializerOptions ScreenCommandServiceJson() => new(JsonSerializerDefaults.Web);

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}

public sealed class AreaCommandDualEntryTests(McpApiTests.Site site) : IClassFixture<McpApiTests.Site>
{
    [Fact]
    public async Task DuplicateNameAndDefaultDeleteAreRejectedByBothEntries()
    {
        using var rest = await site.ClientAsync("admin");
        using var mcp = site.Server.CreateClient();
        var name = "area" + Guid.NewGuid().ToString("N")[..8];

        var created = await rest.PostAsJsonAsync("/api/areas", new { name, description = "rest" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var again = await rest.PostAsJsonAsync("/api/areas", new { name });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("区域名称已存在", await again.Content.ReadAsStringAsync());

        var mcpAgain = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "create_area", new { name }));
        Assert.False(mcpAgain.GetProperty("success").GetBoolean(), mcpAgain.ToString());
        Assert.Contains("区域名称已存在", mcpAgain.GetProperty("message").GetString());

        var mcpName = name + "m";
        var mcpCreated = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "create_area", new { name = mcpName }));
        Assert.True(mcpCreated.GetProperty("success").GetBoolean(), mcpCreated.ToString());
        var restConflict = await rest.PostAsJsonAsync("/api/areas", new { name = mcpName });
        Assert.Equal(HttpStatusCode.BadRequest, restConflict.StatusCode);

        var defaultDelete = await rest.DeleteAsync("/api/areas/" + Area.DefaultId);
        Assert.Equal(HttpStatusCode.BadRequest, defaultDelete.StatusCode);
        Assert.Contains("默认区域不能删除", await defaultDelete.Content.ReadAsStringAsync());
        var mcpDefault = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "delete_area", new { areaId = Area.DefaultId }));
        Assert.False(mcpDefault.GetProperty("success").GetBoolean(), mcpDefault.ToString());
        Assert.Contains("默认区域不能删除", mcpDefault.GetProperty("message").GetString());
    }
}
