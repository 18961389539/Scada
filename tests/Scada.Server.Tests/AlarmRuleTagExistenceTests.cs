using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Data;

namespace Scada.Server.Tests;

/// <summary>
/// 报警规则引用测点存在性：REST、MCP 与 Excel 导入都走 AlarmRuleCommandService；
/// 区域查看权限仍留在 REST 入口，探测无权测点继续返回 403 而不是“不存在”。
/// </summary>
public sealed class AlarmRuleTagExistenceTests(McpApiTests.Site site) : IClassFixture<McpApiTests.Site>
{
    private const int MissingTagId = 1_000_001;

    [Fact]
    public async Task MissingExpressionTagIsRejectedByRestMcpAndExcelImport()
    {
        using var rest = await site.ClientAsync("admin");
        using var mcp = site.Server.CreateClient();
        var expression = $"#{MissingTagId} > 1";
        var message = "缺表达式测点" + Guid.NewGuid().ToString("N")[..8];

        var created = await rest.PostAsJsonAsync("/api/alarm-rules", ExpressionBody(expression, message));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        var (text, issues) = await BodyAsync(created);
        Assert.Contains($"表达式引用测点不存在：{MissingTagId}", text);
        Assert.Equal("alarm.expression.tagNotFound", issues[0].GetProperty("code").GetString());
        Assert.Equal("expression", issues[0].GetProperty("field").GetString());

        var mcpCreated = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "create_alarm_rule", new
        {
            tagId = site.TagA, type = "Expression", level = "Warning", expression, enabled = true, message
        }));
        Assert.False(mcpCreated.GetProperty("success").GetBoolean(), mcpCreated.ToString());
        Assert.Contains($"表达式引用测点不存在：{MissingTagId}", mcpCreated.GetProperty("message").GetString());

        var imported = await ImportCsvAsync(rest, CsvRow(
            "测点ID,类型,表达式,等级,启用,报警信息",
            $"{site.TagA},Expression,{expression},Warning,是,{message}"));
        Assert.Equal(0, imported.GetProperty("created").GetInt32());
        Assert.Equal(1, imported.GetProperty("skipped").GetInt32());
        Assert.Contains($"第 2 行：表达式引用测点不存在：{MissingTagId}",
            imported.GetProperty("errors").EnumerateArray().Select(x => x.GetString()));

        await using var db = await Factory().CreateDbContextAsync();
        Assert.Equal(0, await db.AlarmRules.CountAsync(x => x.Message == message));
    }

    [Fact]
    public async Task MissingSuppressTagIsRejectedByRestMcpAndExcelImport()
    {
        using var rest = await site.ClientAsync("admin");
        using var mcp = site.Server.CreateClient();
        var message = "缺条件测点" + Guid.NewGuid().ToString("N")[..8];

        var created = await rest.PostAsJsonAsync("/api/alarm-rules", SuppressBody(MissingTagId, message));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        var (text, issues) = await BodyAsync(created);
        Assert.Contains($"条件测点不存在：{MissingTagId}", text);
        Assert.Equal("alarm.suppressTag.notFound", issues[0].GetProperty("code").GetString());
        Assert.Equal("suppressTagId", issues[0].GetProperty("field").GetString());

        var mcpCreated = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "create_alarm_rule", new
        {
            tagId = site.TagA, type = "High", level = "Warning", threshold = 1,
            suppressMode = "WhenOff", suppressTagId = MissingTagId, enabled = true, message
        }));
        Assert.False(mcpCreated.GetProperty("success").GetBoolean(), mcpCreated.ToString());
        Assert.Contains($"条件测点不存在：{MissingTagId}", mcpCreated.GetProperty("message").GetString());

        var imported = await ImportCsvAsync(rest, CsvRow(
            "测点ID,类型,阈值,等级,启用,报警信息,条件抑制方式,条件测点ID",
            $"{site.TagA},High,1,Warning,是,{message},WhenOff,{MissingTagId}"));
        Assert.Equal(0, imported.GetProperty("created").GetInt32());
        Assert.Equal(1, imported.GetProperty("skipped").GetInt32());
        Assert.Contains($"第 2 行：条件测点不存在：{MissingTagId}",
            imported.GetProperty("errors").EnumerateArray().Select(x => x.GetString()));

        await using var db = await Factory().CreateDbContextAsync();
        Assert.Equal(0, await db.AlarmRules.CountAsync(x => x.Message == message));
    }

    [Fact]
    public async Task RestKeepsForbiddenWhenProbingUnauthorizedAreaTags()
    {
        using var cfg = await site.ClientAsync("cfgA");
        var expressionMessage = "探测表达式" + Guid.NewGuid().ToString("N")[..8];
        var suppressMessage = "探测抑制" + Guid.NewGuid().ToString("N")[..8];

        var expression = await cfg.PostAsJsonAsync("/api/alarm-rules",
            ExpressionBody($"#{site.TagB} > 10", expressionMessage));
        Assert.Equal(HttpStatusCode.Forbidden, expression.StatusCode);
        Assert.Contains($"无权访问测点：{site.TagB}", await expression.Content.ReadAsStringAsync());

        var suppress = await cfg.PostAsJsonAsync("/api/alarm-rules", SuppressBody(site.TagB, suppressMessage));
        Assert.Equal(HttpStatusCode.Forbidden, suppress.StatusCode);
        Assert.Contains($"无权访问测点：{site.TagB}", await suppress.Content.ReadAsStringAsync());

        var missing = await cfg.PostAsJsonAsync("/api/alarm-rules",
            ExpressionBody($"#{MissingTagId} > 1", "缺测点权限探测" + Guid.NewGuid().ToString("N")[..8]));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var (text, issues) = await BodyAsync(missing);
        Assert.Contains($"表达式引用测点不存在：{MissingTagId}", text);
        Assert.Equal("alarm.expression.tagNotFound", issues[0].GetProperty("code").GetString());
    }

    private IDbContextFactory<ConfigDbContext> Factory() =>
        site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();

    private object ExpressionBody(string expression, string message) => new
    {
        tagId = site.TagA, type = "Expression", threshold = 0, level = "Warning",
        delaySec = 0, recoverDeadband = 0, enabled = true, expression, message
    };

    private object SuppressBody(int suppressTagId, string message) => new
    {
        tagId = site.TagA, type = "High", threshold = 1, level = "Warning",
        delaySec = 0, recoverDeadband = 0, enabled = true, message,
        suppressTagId, suppressMode = "WhenOff"
    };

    private static async Task<(string Message, JsonElement Issues)> BodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("message").GetString() ?? "", body.GetProperty("issues"));
    }

    private static string CsvRow(string header, string row) => header + "\r\n" + row + "\r\n";

    private static async Task<JsonElement> ImportCsvAsync(HttpClient client, string csv)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "alarm-rules.csv");
        var imported = await client.PostAsync("/api/alarm-rules/import", form);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        return await imported.Content.ReadFromJsonAsync<JsonElement>();
    }
}
