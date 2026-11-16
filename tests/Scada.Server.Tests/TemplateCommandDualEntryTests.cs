using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Data;

namespace Scada.Server.Tests;

/// <summary>布局、对象模板与报表模板：REST 与 MCP 走同一命令服务，成功审计与布局发布事务必须一致。</summary>
public sealed class TemplateCommandDualEntryTests(McpConfigurationTests.Site site) : IClassFixture<McpConfigurationTests.Site>
{
    private static object LayoutDocument(int screenId, string title) => new
    {
        monitors = new object[]
        {
            new
            {
                name = "主屏", columns = 12, rows = 12,
                regions = new object[]
                {
                    new { id = "main", kind = "screen", screenId, title, row = 1, col = 1, rowSpan = 8, colSpan = 12 },
                    new { id = "alarms", kind = "alarmList", row = 9, col = 1, rowSpan = 4, colSpan = 12 }
                }
            }
        }
    };

    private object ReportBody(string name, long revision = 0, int? templateId = null) => new
    {
        name, templateId, period = "Day", areaId = site.AreaA, revision, scheduleEnabled = false, shiftStarts = "08:00,20:00",
        columns = new object[]
        {
            new { key = "avg", title = "平均温度", kind = "TagAvg", tagId = site.TagA, decimals = 2, unit = "℃" }
        }
    };

    [Fact]
    public async Task ReportTemplateSaveWritesAuditThroughBothEntries()
    {
        using var rest = await site.ClientAsync("admin");
        using var mcp = site.Server.CreateClient();
        await using var runtime = await site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>().CreateDbContextAsync();
        var restName = "rptr" + Guid.NewGuid().ToString("N")[..8];
        var mcpName = "rptm" + Guid.NewGuid().ToString("N")[..8];

        var created = await Ok(await rest.PostAsJsonAsync("/api/report-templates", ReportBody(restName)));
        var restId = created.GetProperty("id").GetInt32();
        Assert.True(await runtime.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "ReportTemplate" && x.TargetId == restId.ToString() && x.Action.StartsWith("创建报表模板") && x.UserName == "admin"));

        var mcpCreated = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "save_report_template", ReportBody(mcpName)));
        Assert.True(mcpCreated.GetProperty("success").GetBoolean(), mcpCreated.ToString());
        var mcpId = mcpCreated.GetProperty("templateId").GetInt32();
        Assert.True(await runtime.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "ReportTemplate" && x.TargetId == mcpId.ToString() && x.Action.StartsWith("创建报表模板") && x.UserName == "mcp:lan"));
    }

    [Fact]
    public async Task LayoutPublishIsTransactionalAndAuditedOnBothEntries()
    {
        using var rest = await site.ClientAsync("admin");
        using var mcp = site.Server.CreateClient();
        await using var runtime = await site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>().CreateDbContextAsync();
        var factory = site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        var restName = "layr" + Guid.NewGuid().ToString("N")[..8];
        var mcpName = "laym" + Guid.NewGuid().ToString("N")[..8];

        var restDraft = await Ok(await rest.PostAsJsonAsync("/api/hmi/layouts", new { name = restName, document = LayoutDocument(site.PublishedScreenId, "REST") }));
        var restId = restDraft.GetProperty("id").GetInt32();
        var restPublished = await Ok(await rest.PostAsJsonAsync($"/api/hmi/layouts/{restId}/publish", new { revision = restDraft.GetProperty("revision").GetInt32(), note = "REST 发布" }));
        Assert.True(restPublished.GetProperty("publishedRevision").GetInt32() > 0);
        Assert.True(await runtime.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "HmiLayout" && x.TargetId == restId.ToString() && x.Action.StartsWith("发布运行布局") && x.UserName == "admin"));

        var mcpDraft = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "save_hmi_layout", new { name = mcpName, document = LayoutDocument(site.PublishedScreenId, "MCP") }));
        Assert.True(mcpDraft.GetProperty("success").GetBoolean(), mcpDraft.ToString());
        var mcpId = mcpDraft.GetProperty("layoutId").GetInt32();
        var mcpPublished = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "publish_hmi_layout", new { layoutId = mcpId, revision = mcpDraft.GetProperty("revision").GetInt32(), note = "MCP 发布" }));
        Assert.True(mcpPublished.GetProperty("success").GetBoolean(), mcpPublished.ToString());
        Assert.True(await runtime.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "HmiLayout" && x.TargetId == mcpId.ToString() && x.Action.StartsWith("发布运行布局") && x.UserName == "mcp:lan"));

        var failedName = "layf" + Guid.NewGuid().ToString("N")[..8];
        var failedDraft = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "save_hmi_layout", new { name = failedName, document = LayoutDocument(site.PublishedScreenId, "失败") }));
        var failedId = failedDraft.GetProperty("layoutId").GetInt32();
        var failedRevision = failedDraft.GetProperty("revision").GetInt32();
        await using var setup = await factory.CreateDbContextAsync();
#pragma warning disable EF1002
        await setup.Database.ExecuteSqlRawAsync($"CREATE TRIGGER FailMcpLayoutPublish BEFORE INSERT ON HmiLayoutVersion WHEN NEW.LayoutId = {failedId} BEGIN SELECT RAISE(ABORT, 'simulated history failure'); END;");
#pragma warning restore EF1002
        var logCount = await runtime.OperationLogs.CountAsync(x => x.TargetType == "HmiLayout" && x.TargetId == failedId.ToString());
        try
        {
            var raw = await McpApiTests.CallAsync(mcp, "publish_hmi_layout", new { layoutId = failedId, revision = failedRevision, note = "模拟失败" });
            Assert.True(raw.TryGetProperty("isError", out var error) && error.GetBoolean(), raw.ToString());
            await using var verify = await factory.CreateDbContextAsync();
            var row = await verify.HmiLayouts.AsNoTracking().SingleAsync(x => x.Id == failedId);
            Assert.Equal(failedRevision, row.Revision);
            Assert.Null(row.PublishedJson);
            Assert.False(await verify.HmiLayoutVersions.AnyAsync(x => x.LayoutId == failedId));
            Assert.Equal(logCount, await runtime.OperationLogs.CountAsync(x => x.TargetType == "HmiLayout" && x.TargetId == failedId.ToString()));
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER FailMcpLayoutPublish;");
        }
    }

    [Fact]
    public async Task ObjectTemplateSaveAndPublishWriteAuditThroughBothEntries()
    {
        using var rest = await site.ClientAsync("admin");
        using var mcp = site.Server.CreateClient();
        await using var runtime = await site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>().CreateDbContextAsync();
        var pump = await rest.GetFromJsonAsync<JsonElement>("/api/object-templates/examples/pump");
        var restName = "objr" + Guid.NewGuid().ToString("N")[..8];
        var mcpName = "objm" + Guid.NewGuid().ToString("N")[..8];

        var created = await Ok(await rest.PostAsJsonAsync("/api/object-templates", new { name = restName, revision = 0, document = pump }));
        var restId = created.GetProperty("id").GetInt32();
        var published = await Ok(await rest.PostAsJsonAsync($"/api/object-templates/{restId}/publish", new { revision = created.GetProperty("revision").GetInt32() }));
        Assert.Equal(1, published.GetProperty("latestVersion").GetInt32());
        Assert.True(await runtime.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "DeviceObjectTemplate" && x.TargetId == restId.ToString() && x.Action.StartsWith("发布对象模板") && x.UserName == "admin"));

        var mcpSaved = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "save_object_template", new { name = mcpName, document = pump }));
        Assert.True(mcpSaved.GetProperty("success").GetBoolean(), mcpSaved.ToString());
        var mcpId = mcpSaved.GetProperty("templateId").GetInt32();
        var mcpPublished = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "publish_object_template", new { templateId = mcpId, revision = mcpSaved.GetProperty("revision").GetInt32() }));
        Assert.True(mcpPublished.GetProperty("success").GetBoolean(), mcpPublished.ToString());
        Assert.True(await runtime.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "DeviceObjectTemplate" && x.TargetId == mcpId.ToString() && x.Action.StartsWith("发布对象模板") && x.UserName == "mcp:lan"));
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
