using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Mcp;
using HistoryValue = Scada.Core.Models.HistoryValue;

namespace Scada.Server.Tests;

public sealed class McpApiTests : IClassFixture<McpApiTests.Site>
{
    internal static readonly string[] WriteTools =
    [
        "abort_control_sequence", "acknowledge_alarm", "annotate_alarm", "apply_recipe", "confirm_downtime",
        "create_alarm_rule", "create_area", "create_device", "create_screen", "create_tag",
        "delete_alarm_rule", "delete_area", "delete_device", "delete_tag", "generate_report",
        "place_control_lockout", "publish_hmi_layout",
        "publish_object_template", "publish_screen", "reconnect_device", "remove_control_lockout",
        "save_hmi_layout", "save_object_template", "save_report_template", "save_screen_draft",
        "set_device_control_mode", "shelve_alarm_rule", "start_control_sequence", "unshelve_alarm_rule",
        "update_alarm_rule", "update_area", "update_device", "update_tag", "write_tag_value"
    ];
    internal static readonly string[] DestructiveTools =
    [
        "apply_recipe", "delete_alarm_rule", "delete_area", "delete_device", "delete_tag",
        "shelve_alarm_rule", "start_control_sequence", "write_tag_value"
    ];
    internal static readonly string[] ExpectedTools =
    [
        "abort_control_sequence", "acknowledge_alarm", "analyze_alarms", "analyze_history", "annotate_alarm",
        "apply_recipe", "check_recipe_download", "check_tag_write", "confirm_downtime",
        "create_alarm_rule", "create_area", "create_device", "create_screen", "create_tag",
        "delete_alarm_rule", "delete_area", "delete_device", "delete_tag", "generate_report",
        "get_alarm_summary", "get_alarm_timeline", "get_collector_diagnostics",
        "get_device_status", "get_gateway_overview", "get_operations_context", "get_realtime", "list_alarm_rules",
        "list_areas", "list_production_catalog", "list_report_runs", "place_control_lockout", "publish_hmi_layout",
        "publish_object_template", "publish_screen", "query_aggregates", "query_alarms", "query_audit_log",
        "query_downtimes", "query_health", "query_history", "query_oee", "query_report", "query_traceability",
        "read_report_run", "reconnect_device", "remove_control_lockout", "save_hmi_layout", "save_object_template",
        "save_report_template", "save_screen_draft", "search_tags", "set_device_control_mode", "shelve_alarm_rule",
        "start_control_sequence", "unshelve_alarm_rule", "update_alarm_rule", "update_area", "update_device",
        "update_tag", "write_tag_value"
    ];
    private readonly Site _site;
    public McpApiTests(Site site) => _site = site;

    public class Site : AuthorizationTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder builder)
        {
            builder.UseSetting("Mcp:RequestsPerMinute", "1000");
            // 聚合用例直接写入已汇总的小时/5 分钟桶；后台聚合任务会在启动 5 秒后按原始数据重算并覆盖，
            // 机器一忙用例就跨过这个时间点，断言值会变成用原始样本重算的结果。测试站点不需要后台聚合。
            builder.UseSetting("Scada:AggregationEnabled", "false");
        }
    }

    internal static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, object? parameters = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, McpEndpoints.Path)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } })
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        return client.SendAsync(request);
    }

    internal static async Task<JsonElement> EnvelopeAsync(HttpClient client, string method, object? parameters = null)
    {
        using var response = await SendAsync(client, method, parameters);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').First(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    internal static async Task<JsonElement> RpcAsync(HttpClient client, string method, object? parameters = null)
    {
        var envelope = await EnvelopeAsync(client, method, parameters);
        Assert.False(envelope.TryGetProperty("error", out var error), error.ToString());
        return envelope.GetProperty("result");
    }

    internal static Task<JsonElement> CallAsync(HttpClient client, string name, object? arguments = null) =>
        RpcAsync(client, "tools/call", new { name, arguments = arguments ?? new { } });

    internal static JsonElement Data(JsonElement result)
    {
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.ToString());
        var data = result.GetProperty("structuredContent");
        Assert.False(data.TryGetProperty("result", out _), data.ToString());
        return data;
    }

    [Fact]
    public async Task Initialize_And_List_Expose_Accurately_Annotated_Tools()
    {
        Assert.True(new McpOptions().Enabled);
        var client = _site.Server.CreateClient();
        using (client)
        {
            var init = await RpcAsync(client, "initialize", new
            {
                protocolVersion = "2025-11-25", capabilities = new { },
                clientInfo = new { name = "scada-tests", version = "1.0" }
            });
            Assert.Equal("Scada", init.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.True(init.GetProperty("capabilities").TryGetProperty("tools", out _));
            Assert.True(init.GetProperty("capabilities").TryGetProperty("resources", out _));
            Assert.True(init.GetProperty("capabilities").TryGetProperty("prompts", out _));
            var list = await RpcAsync(client, "tools/list");
            var tools = list.GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(ExpectedTools,
                tools.Select(x => x.GetProperty("name").GetString()).OrderBy(x => x));
            foreach (var tool in tools)
            {
                var name = tool.GetProperty("name").GetString()!;
                var write = WriteTools.Contains(name);
                var annotations = tool.GetProperty("annotations");
                Assert.Equal(!write, annotations.GetProperty("readOnlyHint").GetBoolean());
                Assert.Equal(DestructiveTools.Contains(name), annotations.GetProperty("destructiveHint").GetBoolean());
                Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
                Assert.Equal("object", tool.GetProperty("outputSchema").GetProperty("type").GetString());
                var schema = tool.GetProperty("inputSchema").ToString();
                Assert.DoesNotContain("cancellationToken", schema);
                Assert.DoesNotContain("httpContext", schema);
            }
            // 幂等键必须出现在 inputSchema 里，客户端才能发现并复用；未声明的工具不应暴露该参数。
            string[] idempotent =
            [
                "create_area", "create_device", "create_tag", "create_alarm_rule",
                "place_control_lockout", "start_control_sequence", "apply_recipe", "generate_report",
                // write_tag_value 把 requestId 交给控制闭环，不是幂等缓存，但参数同名同义。
                "write_tag_value"
            ];
            foreach (var tool in tools)
            {
                var name = tool.GetProperty("name").GetString()!;
                var hasRequestId = tool.GetProperty("inputSchema").ToString().Contains("\"requestId\"", StringComparison.Ordinal);
                Assert.Equal(idempotent.Contains(name), hasRequestId);
            }
        }
    }

    [Fact]
    public async Task Official_Sdk_Can_Connect_List_And_Call_With_Structured_Content()
    {
        var http = _site.Server.CreateClient();
        using (http)
        {
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, McpEndpoints.Path),
                    TransportMode = HttpTransportMode.StreamableHttp
                }, http));
            var tools = await client.ListToolsAsync();
            Assert.Equal(ExpectedTools.Length, tools.Count);
            var result = await client.CallToolAsync("search_tags", new Dictionary<string, object?> { ["keyword"] = "A温度" });
            Assert.NotEqual(true, result.IsError);
            Assert.NotNull(result.StructuredContent);
            Assert.Equal("A温度", Assert.Single(result.StructuredContent.Value.GetProperty("items").EnumerateArray())
                .GetProperty("name").GetString());
            Assert.Equal(4, (await client.ListResourcesAsync()).Count);
            Assert.Equal(2, (await client.ListResourceTemplatesAsync()).Count);
            var guide = await client.ReadResourceAsync("scada://guide");
            Assert.Contains("mcp:lan", Assert.IsType<TextResourceContents>(Assert.Single(guide.Contents)).Text);
            Assert.Equal(2, (await client.ListPromptsAsync()).Count);
            var prompt = await client.GetPromptAsync("inspect_device", new Dictionary<string, object?> { ["deviceId"] = _site.DeviceA.ToString() });
            Assert.Contains("get_collector_diagnostics", Assert.IsType<TextContentBlock>(Assert.Single(prompt.Messages).Content).Text);
        }
    }

    [Fact]
    public async Task Anonymous_Mcp_Does_Not_Require_Credentials_Or_Change_Other_Api_Authentication()
    {
        using var anonymous = _site.Server.CreateClient();
        await RpcAsync(anonymous, "tools/list");
        Data(await CallAsync(anonymous, "search_tags"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/commands", new { tagId = _site.TagA, value = 99 })).StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "scada_invalid");
        await RpcAsync(anonymous, "tools/list");
        using var jwt = await _site.ClientAsync("opA");
        var mcpTags = Data(await CallAsync(jwt, "search_tags")).GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(mcpTags, x => x.GetProperty("id").GetInt32() == _site.TagB);
        var uiTags = await jwt.GetFromJsonAsync<JsonElement[]>("/api/tags");
        Assert.DoesNotContain(uiTags!, x => x.GetProperty("id").GetInt32() == _site.TagB);
    }

    [Fact]
    public async Task Anonymous_Queries_Read_All_Areas_But_Nonexistent_Tags_Fail_Closed()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            var tags = Data(await CallAsync(client, "search_tags")).GetProperty("items").EnumerateArray().ToArray();
            Assert.Contains(tags, x => x.GetProperty("id").GetInt32() == _site.TagA);
            Assert.Contains(tags, x => x.GetProperty("id").GetInt32() == _site.TagB);
            Assert.Single(Data(await CallAsync(client, "search_tags", new { keyword = "B温度" })).GetProperty("items").EnumerateArray());
            var devices = Data(await CallAsync(client, "get_device_status")).GetProperty("items").EnumerateArray().ToArray();
            Assert.Contains(devices, x => x.GetProperty("id").GetInt32() == _site.DeviceA);
            Assert.Contains(devices, x => x.GetProperty("id").GetInt32() == _site.DeviceB);
            Assert.DoesNotContain("password", JsonSerializer.Serialize(devices), StringComparison.OrdinalIgnoreCase);

            var cache = _site.Services.GetRequiredService<IRealtimeCache>();
            cache.Set(new TagValue(_site.TagA, 12.5, TagQuality.Good, ScadaTime.NowMs()));
            cache.Set(new TagValue(_site.TagB, 99, TagQuality.Good, ScadaTime.NowMs()));
            var values = Data(await CallAsync(client, "get_realtime", new { tagIds = new[] { _site.TagA } }));
            Assert.Equal(12.5, values.GetProperty("values")[0].GetProperty("value").GetDouble());
            Assert.Equal("Good", values.GetProperty("values")[0].GetProperty("quality").GetString());
            cache.Remove(_site.TagA);
            var missing = Data(await CallAsync(client, "get_realtime", new { tagIds = new[] { _site.TagA } }));
            Assert.Empty(missing.GetProperty("values").EnumerateArray());
            Assert.Equal(_site.TagA, missing.GetProperty("missingTagIds")[0].GetInt32());

            var otherArea = Data(await CallAsync(client, "get_realtime", new { tagIds = new[] { _site.TagA, _site.TagB } }));
            Assert.Equal(_site.TagB, Assert.Single(otherArea.GetProperty("values").EnumerateArray()).GetProperty("tagId").GetInt32());
            Data(await CallAsync(client, "query_history", new { tagIds = new[] { _site.TagB }, from = 0L, to = 1000L }));
            Assert.Equal("not_found", (await CallAsync(client, "get_realtime", new { tagIds = new[] { _site.TagA, int.MaxValue } }))
                .GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("not_found", (await CallAsync(client, "query_history", new { tagIds = new[] { int.MaxValue }, from = 0L, to = 1000L }))
                .GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Tool_Success_And_Failure_Are_Audited_Without_Requiring_A_User()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            Data(await CallAsync(client, "search_tags"));
            Assert.True((await CallAsync(client, "query_history", new { tagIds = new[] { _site.TagA }, from = 1000, to = 0 })).GetProperty("isError").GetBoolean());
            var success = await _site.LastLogAsync(x => x.TargetType == "McpTool" && x.Action == "MCP search_tags");
            var failed = await _site.LastLogAsync(x => x.TargetType == "McpTool" && x.Action == "MCP query_history");
            Assert.NotNull(success);
            Assert.Equal("成功", success.Result);
            Assert.Equal("mcp:lan", success.UserName);
            Assert.Null(success.UserId);
            Assert.False(string.IsNullOrEmpty(success.TargetId));
            Assert.Null(success.Details);
            Assert.NotNull(failed);
            Assert.StartsWith("失败", failed.Result);
        }
    }

    [Theory]
    [InlineData("write_tag")]
    [InlineData("ack_alarm")]
    [InlineData("run_script")]
    public async Task Write_And_Control_Tools_Remain_Unavailable(string name)
    {
        using var client = _site.Server.CreateClient();
        var response = await EnvelopeAsync(client, "tools/call", new { name, arguments = new { tagId = _site.TagA, value = 99 } });
        Assert.True(response.TryGetProperty("error", out _) ||
            response.GetProperty("result").GetProperty("isError").GetBoolean(), response.ToString());
    }

    [Theory]
    [InlineData("search_tags", "{\"limit\":101}")]
    [InlineData("search_tags", "{\"afterId\":-1}")]
    [InlineData("get_realtime", "{\"tagIds\":[]}")]
    [InlineData("get_realtime", "{\"tagIds\":[0]}")]
    [InlineData("get_realtime", "{\"tagIds\":null}")]
    [InlineData("query_history", "{\"tagIds\":[1],\"from\":1000,\"to\":0}")]
    [InlineData("query_history", "{\"tagIds\":[1],\"from\":0,\"to\":2764800000}")]
    [InlineData("query_history", "{\"tagIds\":[1],\"from\":0,\"to\":1000,\"maxPoints\":1001}")]
    [InlineData("query_history", "{\"tagIds\":[1],\"from\":0,\"to\":1000,\"maxPoints\":19}")]
    [InlineData("query_history", "{\"tagIds\":[1],\"from\":-9223372036854775808,\"to\":9223372036854775807}")]
    [InlineData("query_alarms", "{\"activeOnly\":false}")]
    [InlineData("query_alarms", "{\"activeOnly\":true,\"from\":0,\"to\":1000}")]
    [InlineData("query_alarms", "{\"offset\":10001}")]
    public async Task Invalid_Inputs_Return_Mcp_Tool_Errors(string tool, string arguments)
    {
        var client = _site.Server.CreateClient();
        using (client)
        using (var json = JsonDocument.Parse(arguments))
        {
            var result = await CallAsync(client, tool, json.RootElement);
            Assert.True(result.GetProperty("isError").GetBoolean(), result.ToString());
            Assert.Equal("invalid_argument",
                result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public void Unexpected_Exceptions_Become_Internal_Error_Results()
    {
        var result = McpEndpoints.Failure(new InvalidOperationException("boom"));
        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(McpErrorCodes.Internal, error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        // 原始异常文本不进入客户端可见文案，避免泄漏内部实现细节。
        Assert.DoesNotContain("boom", error.GetProperty("message").GetString()!);

        var known = McpEndpoints.Failure(new McpToolException(McpErrorCodes.QueryBusy, "busy", true));
        var busy = known.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(McpErrorCodes.QueryBusy, busy.GetProperty("code").GetString());
        Assert.True(busy.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task Tool_Results_Expose_DataAsOf_And_Truncation()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            var started = ScadaTime.NowMs();
            var calls = new (string Name, object? Arguments)[]
            {
                ("search_tags", null), ("get_realtime", new { tagIds = new[] { _site.TagA } }),
                ("query_alarms", null), ("query_health", null), ("get_alarm_summary", null),
                ("list_areas", null), ("get_gateway_overview", null)
            };
            foreach (var (name, arguments) in calls)
            {
                var data = Data(await CallAsync(client, name, arguments));
                var dataAsOf = data.GetProperty("dataAsOf").GetInt64();
                Assert.InRange(dataAsOf, started - 60_000, ScadaTime.NowMs() + 60_000);
                Assert.False(data.GetProperty("truncated").GetBoolean(), name);
            }
        }
    }

    [Fact]
    public async Task History_Total_Point_Budget_Is_Enforced_For_Multiple_Tags()
    {
        var from = ScadaTime.NowMs() - 3_600_000;
        var samples = Enumerable.Range(0, 2000).SelectMany(i => new[]
        {
            new HistoryValue(_site.TagA, from + i, i == 1000 ? 999 : 1, TagQuality.Good),
            new HistoryValue(_site.TagB, from + i, i == 1001 ? 888 : 2, TagQuality.Bad)
        }).ToArray();
        await _site.Services.GetRequiredService<IHistoryStore>().WriteRawAsync(samples, default);
        var client = _site.Server.CreateClient();
        using (client)
        {
            var data = Data(await CallAsync(client, "query_history", new { tagIds = new[] { _site.TagA, _site.TagB }, from, to = from + 1999, maxPoints = 20 }));
            Assert.True(data.GetProperty("downsampled").GetBoolean());
            Assert.Equal(20, data.GetProperty("pointLimit").GetInt32());
            var series = data.GetProperty("series").EnumerateArray().ToArray();
            Assert.Equal(2, series.Length);
            Assert.InRange(series.Sum(x => x.GetProperty("points").GetArrayLength()), 4, 20);
            foreach (var item in series)
            {
                var points = item.GetProperty("points").EnumerateArray().ToArray();
                Assert.Equal(from, points[0].GetProperty("ts").GetInt64());
                Assert.Equal(from + 1999, points[^1].GetProperty("ts").GetInt64());
            }
        }
    }

    [Fact]
    public async Task Anonymous_Alarm_Query_Includes_All_Areas_And_History_Is_ReadOnly()
    {
        var factory = _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var historical = new AlarmEvent { TagId = _site.TagA, DeviceId = _site.DeviceA, Message = "returned", StartTs = 1000, EndTs = 2000 };
        var otherArea = new AlarmEvent { TagId = _site.TagB, DeviceId = _site.DeviceB, Message = "other area", StartTs = 1500, EndTs = 2500 };
        db.AlarmEvents.AddRange(historical, otherArea);
        await db.SaveChangesAsync();
        var client = _site.Server.CreateClient();
        using (client)
        {
            var active = Data(await CallAsync(client, "query_alarms", new { limit = 100 })).GetProperty("items");
            Assert.Contains(active.EnumerateArray(), x => x.GetProperty("id").GetInt32() == _site.AlarmA);
            Assert.Contains(active.EnumerateArray(), x => x.GetProperty("id").GetInt32() == _site.AlarmB);
            var first = Data(await CallAsync(client, "query_alarms", new { activeOnly = false, from = 0, to = 3000, limit = 1 }));
            Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
            var second = Data(await CallAsync(client, "query_alarms", new { activeOnly = false, from = 0, to = 3000, offset = 1, limit = 1 }));
            var returnedIds = new[] { first.GetProperty("items")[0].GetProperty("id").GetInt32(), second.GetProperty("items")[0].GetProperty("id").GetInt32() };
            Assert.Contains(historical.Id, returnedIds);
            Assert.Contains(otherArea.Id, returnedIds);
            Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
            Assert.Null((await db.AlarmEvents.AsNoTracking().SingleAsync(x => x.Id == _site.AlarmA)).AckTs);
        }
    }

    [Fact]
    public async Task Tag_Paging_And_Request_Limits_Are_Enforced()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            var first = Data(await CallAsync(client, "search_tags", new { limit = 1 }));
            var cursor = first.GetProperty("nextAfterId").GetInt32();
            var second = Data(await CallAsync(client, "search_tags", new { limit = 1, afterId = cursor }));
            Assert.True(second.GetProperty("items")[0].GetProperty("id").GetInt32() > cursor);
            Assert.True((await CallAsync(client, "get_realtime", new { tagIds = Enumerable.Repeat(_site.TagA, 101).ToArray() })).GetProperty("isError").GetBoolean());
            client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, "tools/list")).StatusCode);
            client.DefaultRequestHeaders.Remove("Origin");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
                (await SendAsync(client, "tools/call", new { name = "search_tags", arguments = new { keyword = new string('x', 70000) } })).StatusCode);
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
            Data(await CallAsync(client, "search_tags"));
        }
    }

    [Theory]
    [InlineData(65536, HttpStatusCode.OK)]
    [InlineData(65537, HttpStatusCode.RequestEntityTooLarge)]
    public async Task Body_Limit_Accepts_Exactly_64KiB_And_Rejects_One_Byte_More(int bytes, HttpStatusCode expected)
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            const string json = """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""";
            using var request = new HttpRequestMessage(HttpMethod.Post, McpEndpoints.Path)
            {
                Content = new StringContent(json.PadRight(bytes), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Fact]
    public async Task Alarm_Paging_Reports_Truncation_Instead_Of_An_Unusable_Cursor()
    {
        var factory = _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var now = ScadaTime.NowMs();
        db.AlarmEvents.AddRange(Enumerable.Range(0, 10102).Select(i => new AlarmEvent
        {
            TagId = _site.TagB, DeviceId = _site.DeviceB, Message = "paging budget", StartTs = now + i, EndTs = now + i + 1
        }));
        await db.SaveChangesAsync();
        var client = _site.Server.CreateClient();
        using (client)
        {
            var page = Data(await CallAsync(client, "query_alarms", new { activeOnly = false, from = now, to = now + 20000, offset = 10000, limit = 100 }));
            Assert.Equal(100, page.GetProperty("items").GetArrayLength());
            Assert.True(page.GetProperty("truncated").GetBoolean());
            Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
        }
    }
}

public sealed class McpMissingSettingTests : IClassFixture<McpMissingSettingTests.Site>
{
    private readonly Site _site;
    public McpMissingSettingTests(Site site) => _site = site;

    public sealed class Site : AuthorizationTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = configuration.Build().AsEnumerable()
                    .Where(x => !string.Equals(x.Key, "Mcp:Enabled", StringComparison.OrdinalIgnoreCase)).ToArray();
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(settings);
            });
    }

    [Fact]
    public async Task Missing_Enable_Setting_Still_Registers_And_Serves_Anonymous_Mcp()
    {
        Assert.Null(_site.Services.GetRequiredService<IConfiguration>()["Mcp:Enabled"]);
        using var client = _site.Server.CreateClient();
        var result = await McpApiTests.RpcAsync(client, "tools/list");
        Assert.Equal(McpApiTests.ExpectedTools.Length, result.GetProperty("tools").GetArrayLength());
        McpApiTests.Data(await McpApiTests.CallAsync(client, "search_tags"));
    }
}

public sealed class McpDisabledTests : IClassFixture<McpDisabledTests.Site>
{
    private readonly Site _site;
    public McpDisabledTests(Site site) => _site = site;

    public sealed class Site : AuthorizationTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder builder) =>
            builder.UseSetting("Mcp:Enabled", "false");
    }

    [Fact]
    public async Task Explicit_Disable_Returns_NotFound_While_Existing_Apis_Work()
    {
        using var anonymous = _site.Server.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await McpApiTests.SendAsync(anonymous, "tools/list")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(McpEndpoints.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tags")).StatusCode);
        using var client = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.NotFound, (await McpApiTests.SendAsync(client, "tools/list")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(McpEndpoints.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tags")).StatusCode);
    }
}

public sealed class McpRateLimitTests : IClassFixture<McpRateLimitTests.Site>
{
    private readonly Site _site;
    public McpRateLimitTests(Site site) => _site = site;

    public sealed class Site : McpApiTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder builder)
        {
            base.ConfigureSettings(builder);
            builder.UseSetting("Mcp:RequestsPerMinute", "2");
        }
    }

    [Fact]
    public async Task Anonymous_Rate_Limit_And_Audit_Use_Connection_Address_Not_Forwarded_Headers()
    {
        async Task<Microsoft.AspNetCore.Http.HttpContext> Send(string ip, string forwarded)
        {
            var result = await _site.Server.SendAsync(http =>
            {
                http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                http.Request.Method = "POST";
                http.Request.Path = McpEndpoints.Path;
                http.Request.ContentType = "application/json";
                http.Request.Headers.Accept = "application/json, text/event-stream";
                http.Request.Headers["MCP-Protocol-Version"] = "2025-11-25";
                http.Request.Headers["X-Forwarded-For"] = forwarded;
                http.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_tags","arguments":{}}}"""));
            });
            await result.Response.Body.CopyToAsync(Stream.Null);
            return result;
        }

        Assert.Equal(200, (await Send("192.168.10.11", "192.168.10.21")).Response.StatusCode);
        Assert.Equal(200, (await Send("192.168.10.11", "192.168.10.22")).Response.StatusCode);
        var limited = await Send("192.168.10.11", "192.168.10.23");
        Assert.Equal(429, limited.Response.StatusCode);
        Assert.True(limited.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal(200, (await Send("192.168.10.12", "192.168.10.23")).Response.StatusCode);
        var source = IPAddress.Parse("192.168.10.12").MapToIPv6().ToString();
        var audit = await _site.LastLogAsync(x => x.TargetType == "McpTool" && x.TargetId == source);
        Assert.NotNull(audit);
        Assert.Equal("mcp:lan", audit.UserName);
    }
}
