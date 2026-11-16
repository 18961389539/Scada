using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Data;
using static Scada.Server.Tests.McpApiTests;

namespace Scada.Server.Tests;

/// <summary>
/// 控制挂牌双入口：REST 与 MCP 走同一命令服务，但摘牌/检修牌策略保持入口差异。
/// </summary>
public sealed class ControlLockoutCommandDualEntryTests(McpWriteTests.Site site) : IClassFixture<McpWriteTests.Site>
{
    private IDbContextFactory<RuntimeDbContext> Runtime => site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();

    [Fact]
    public async Task RestAllowsMaintenanceAndAreaAdminRemovalWhileMcpIsOwnProhibitOnly()
    {
        using var admin = await site.ClientAsync("admin");
        using var op = await site.ClientAsync("opA");
        using var cfg = await site.ClientAsync("cfgA");
        using var watch = await site.ClientAsync("watchA");
        using var mcp = site.Server.CreateClient();

        var own = await Ok(await op.PostAsJsonAsync("/api/control/lockouts", new { tagId = site.TagA, kind = "NoOperate", reason = "操作员自挂" + U() }));
        var ownId = own.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await watch.PostAsJsonAsync($"/api/control/lockouts/{ownId}/remove", new { reason = "同区域操作员但非挂牌人" })).StatusCode);
        await Ok(await op.PostAsJsonAsync($"/api/control/lockouts/{ownId}/remove", new { reason = "挂牌人自摘" }));

        var maint = await Ok(await admin.PostAsJsonAsync("/api/control/lockouts", new { deviceId = site.DeviceA, kind = "Maintenance", reason = "检修代摘" + U() }));
        var maintId = maint.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/control/lockouts/{maintId}/remove", new { reason = "非挂牌人" })).StatusCode);
        var mcpMaint = McpApiTests.Data(await CallAsync(mcp, "place_control_lockout", new { deviceId = site.DeviceA, kind = "Maintenance", reason = "MCP 检修" }));
        Assert.False(mcpMaint.GetProperty("success").GetBoolean(), mcpMaint.ToString());
        var mcpRemoveMaint = McpApiTests.Data(await CallAsync(mcp, "remove_control_lockout", new { lockoutId = maintId, reason = "MCP 不得代摘检修牌" }));
        Assert.False(mcpRemoveMaint.GetProperty("success").GetBoolean(), mcpRemoveMaint.ToString());
        Assert.Contains("只能摘除 MCP 自己挂的禁止操作牌", mcpRemoveMaint.GetProperty("message").GetString());
        await Ok(await cfg.PostAsJsonAsync($"/api/control/lockouts/{maintId}/remove", new { reason = "区域管理员代摘" }));

        var restNo = await Ok(await admin.PostAsJsonAsync("/api/control/lockouts", new { tagId = site.TagA, kind = "NoOperate", reason = "网页禁止" + U() }));
        var restNoId = restNo.GetProperty("id").GetInt32();
        var mcpRemoveForeign = McpApiTests.Data(await CallAsync(mcp, "remove_control_lockout", new { lockoutId = restNoId, reason = "MCP 不得摘他人牌" }));
        Assert.False(mcpRemoveForeign.GetProperty("success").GetBoolean(), mcpRemoveForeign.ToString());
        Assert.Contains("只能摘除 MCP 自己挂的禁止操作牌", mcpRemoveForeign.GetProperty("message").GetString());

        var mcpPlaced = McpApiTests.Data(await CallAsync(mcp, "place_control_lockout", new { tagId = site.TagA, kind = "NoOperate", reason = "MCP 自挂" + U() }));
        Assert.True(mcpPlaced.GetProperty("success").GetBoolean(), mcpPlaced.ToString());
        var mcpId = mcpPlaced.GetProperty("lockoutId").GetInt32();
        Assert.True(McpApiTests.Data(await CallAsync(mcp, "remove_control_lockout", new { lockoutId = mcpId, reason = "MCP 自摘" })).GetProperty("success").GetBoolean());

        var mcpAgain = McpApiTests.Data(await CallAsync(mcp, "place_control_lockout", new { tagId = site.TagA, kind = "NoOperate", reason = "MCP 再挂" + U() }));
        var mcpAgainId = mcpAgain.GetProperty("lockoutId").GetInt32();
        // 网页区域管理员仍可摘 MCP 挂的牌：策略不合并。
        await Ok(await cfg.PostAsJsonAsync($"/api/control/lockouts/{mcpAgainId}/remove", new { reason = "网页代摘 MCP 挂牌" }));
        await Ok(await admin.PostAsJsonAsync($"/api/control/lockouts/{restNoId}/remove", new { reason = "清理网页禁止牌" }));
    }

    [Fact]
    public async Task OverlongRemoveReasonIsRejectedByBothEntriesInsteadOfTruncated()
    {
        using var admin = await site.ClientAsync("admin");
        using var mcp = site.Server.CreateClient();
        var overlong = new string('摘', 201);
        var exact = new string('说', 200);

        var rest = await Ok(await admin.PostAsJsonAsync("/api/control/lockouts", new { tagId = site.TagA, kind = "NoOperate", reason = "超长摘牌" + U() }));
        var restId = rest.GetProperty("id").GetInt32();
        using (var rejected = await admin.PostAsJsonAsync($"/api/control/lockouts/{restId}/remove", new { reason = overlong }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var body = await rejected.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("最多 200 字", body.GetProperty("message").GetString());
        }
        await using (var runtime = await Runtime.CreateDbContextAsync())
        {
            var row = await runtime.ControlLockouts.AsNoTracking().SingleAsync(x => x.Id == restId);
            Assert.Null(row.RemovedTs);
            Assert.Null(row.RemoveReason);
        }
        await Ok(await admin.PostAsJsonAsync($"/api/control/lockouts/{restId}/remove", new { reason = exact }));

        var mcpPlaced = McpApiTests.Data(await CallAsync(mcp, "place_control_lockout", new { tagId = site.TagA, kind = "NoOperate", reason = "MCP 超长摘牌" + U() }));
        Assert.True(mcpPlaced.GetProperty("success").GetBoolean(), mcpPlaced.ToString());
        var mcpId = mcpPlaced.GetProperty("lockoutId").GetInt32();
        var mcpRejected = await CallAsync(mcp, "remove_control_lockout", new { lockoutId = mcpId, reason = overlong });
        Assert.True(mcpRejected.TryGetProperty("isError", out var isError) && isError.GetBoolean(), mcpRejected.ToString());
        Assert.Equal("invalid_argument", mcpRejected.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        await using (var runtime = await Runtime.CreateDbContextAsync())
        {
            var row = await runtime.ControlLockouts.AsNoTracking().SingleAsync(x => x.Id == mcpId);
            Assert.Null(row.RemovedTs);
            Assert.Null(row.RemoveReason);
        }
        var mcpRemoved = McpApiTests.Data(await CallAsync(mcp, "remove_control_lockout", new { lockoutId = mcpId, reason = exact }));
        Assert.True(mcpRemoved.GetProperty("success").GetBoolean(), mcpRemoved.ToString());
    }

    private static string U() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
