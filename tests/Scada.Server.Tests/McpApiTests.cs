using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.External;
using Scada.Server.Mcp;
using HistoryValue = Scada.Core.Models.HistoryValue;

namespace Scada.Server.Tests;

public sealed class McpApiTests : IClassFixture<McpApiTests.Site>
{
    private readonly Site _site;
    public McpApiTests(Site site) => _site = site;

    public class Site : AuthorizationTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder builder)
        {
            builder.UseSetting("Mcp:Enabled", "true");
            builder.UseSetting("Mcp:RequestsPerMinute", "1000");
        }
    }

    internal static async Task<(HttpClient Client, ExternalApiKeyCreated Key)> KeyClientAsync(
        AuthorizationTests.Site site, ExternalApiScope scopes, int rate = 1000, bool allAreas = false)
    {
        var service = site.Services.GetRequiredService<ExternalApiKeyService>();
        var key = await service.CreateAsync("mcp-" + Guid.NewGuid().ToString("N"), scopes,
            allAreas ? null : [site.AreaA], rate, null, "admin", CancellationToken.None);
        var client = site.Server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        return (client, key);
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

    internal static async Task<JsonElement> RpcAsync(HttpClient client, string method, object? parameters = null)
    {
        using var response = await SendAsync(client, method, parameters);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').First(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("error", out var error), error.ToString());
        return document.RootElement.GetProperty("result").Clone();
    }

    internal static Task<JsonElement> CallAsync(HttpClient client, string name, object? arguments = null) =>
        RpcAsync(client, "tools/call", new { name, arguments = arguments ?? new { } });

    internal static JsonElement Data(JsonElement result)
    {
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean(), result.ToString());
        return result.GetProperty("structuredContent");
    }

    [Fact]
    public async Task Initialize_And_List_Expose_Only_Annotated_ReadOnly_Tools()
    {
        var (client, _) = await KeyClientAsync(_site, ExternalApiScope.ReadTags);
        using (client)
        {
            var init = await RpcAsync(client, "initialize", new
            {
                protocolVersion = "2025-11-25", capabilities = new { },
                clientInfo = new { name = "scada-tests", version = "1.0" }
            });
            Assert.Equal("Scada", init.GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.True(init.GetProperty("capabilities").TryGetProperty("tools", out _));
            var list = await RpcAsync(client, "tools/list");
            var tools = list.GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(new[] { "get_device_status", "get_realtime", "query_alarms", "query_history", "search_tags" },
                tools.Select(x => x.GetProperty("name").GetString()).OrderBy(x => x));
            foreach (var tool in tools)
            {
                var annotations = tool.GetProperty("annotations");
                Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
                Assert.False(annotations.GetProperty("destructiveHint").GetBoolean());
                Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
                var schema = tool.GetProperty("inputSchema").ToString();
                Assert.DoesNotContain("cancellationToken", schema);
                Assert.DoesNotContain("httpContext", schema);
            }
        }
    }

    [Fact]
    public async Task Official_Sdk_Can_Connect_List_And_Call_With_Structured_Content()
    {
        var (http, _) = await KeyClientAsync(_site, ExternalApiScope.ReadTags);
        using (http)
        {
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, McpEndpoints.Path),
                    TransportMode = HttpTransportMode.StreamableHttp
                }, http));
            var tools = await client.ListToolsAsync();
            Assert.Equal(5, tools.Count);
            var result = await client.CallToolAsync("search_tags", new Dictionary<string, object?> { ["keyword"] = "A温度" });
            Assert.NotEqual(true, result.IsError);
            Assert.NotNull(result.StructuredContent);
            Assert.Equal("A温度", Assert.Single(result.StructuredContent.Value.GetProperty("items").EnumerateArray())
                .GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Anonymous_Invalid_And_Revoked_Keys_Are_Rejected_And_Jwt_Is_OptIn()
    {
        using var anonymous = _site.Server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "tools/list")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "scada_invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, "tools/list")).StatusCode);
        using var jwt = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(jwt, "tools/list")).StatusCode);

        var (client, key) = await KeyClientAsync(_site, ExternalApiScope.ReadTags);
        using (client)
        {
            await RpcAsync(client, "tools/list");
            await _site.Services.GetRequiredService<ExternalApiKeyService>().RevokeAsync(key.Key.Id, "admin", default);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, "tools/list")).StatusCode);
        }
    }

    [Fact]
    public async Task Queries_Enforce_Areas_And_Explicit_Batches_Fail_Closed()
    {
        var (client, _) = await KeyClientAsync(_site, ExternalApiScope.ReadTags | ExternalApiScope.ReadHistory);
        using (client)
        {
            var tags = Data(await CallAsync(client, "search_tags")).GetProperty("items").EnumerateArray().ToArray();
            Assert.Contains(tags, x => x.GetProperty("id").GetInt32() == _site.TagA);
            Assert.DoesNotContain(tags, x => x.GetProperty("id").GetInt32() == _site.TagB);
            Assert.Empty(Data(await CallAsync(client, "search_tags", new { keyword = "B温度" })).GetProperty("items").EnumerateArray());
            var devices = Data(await CallAsync(client, "get_device_status")).GetProperty("items").EnumerateArray().ToArray();
            Assert.Contains(devices, x => x.GetProperty("id").GetInt32() == _site.DeviceA);
            Assert.DoesNotContain(devices, x => x.GetProperty("id").GetInt32() == _site.DeviceB);
            Assert.DoesNotContain("password", JsonSerializer.Serialize(devices), StringComparison.OrdinalIgnoreCase);

            var cache = _site.Services.GetRequiredService<IRealtimeCache>();
            cache.Set(new TagValue(_site.TagA, 12.5, TagQuality.Good, ScadaTime.NowMs()));
            var values = Data(await CallAsync(client, "get_realtime", new { tagIds = new[] { _site.TagA } }));
            Assert.Equal(12.5, values.GetProperty("values")[0].GetProperty("value").GetDouble());
            Assert.Equal("Good", values.GetProperty("values")[0].GetProperty("quality").GetString());
            cache.Remove(_site.TagA);
            var missing = Data(await CallAsync(client, "get_realtime", new { tagIds = new[] { _site.TagA } }));
            Assert.Empty(missing.GetProperty("values").EnumerateArray());
            Assert.Equal(_site.TagA, missing.GetProperty("missingTagIds")[0].GetInt32());

            Assert.True((await CallAsync(client, "get_realtime", new { tagIds = new[] { _site.TagA, _site.TagB } })).GetProperty("isError").GetBoolean());
            Assert.True((await CallAsync(client, "query_history", new { tagIds = new[] { _site.TagB }, from = 0L, to = 1000L })).GetProperty("isError").GetBoolean());
        }
    }

    [Fact]
    public async Task Scopes_Unknown_Tools_And_Both_Outcomes_Are_Audited_Without_Secrets()
    {
        var (client, key) = await KeyClientAsync(_site, ExternalApiScope.ReadTags);
        using (client)
        {
            Data(await CallAsync(client, "search_tags"));
            Assert.True((await CallAsync(client, "query_alarms")).GetProperty("isError").GetBoolean());
            Assert.True((await CallAsync(client, "write_tag", new { tagId = _site.TagA, value = 99 })).GetProperty("isError").GetBoolean());
            var success = await _site.LastLogAsync(x => x.TargetType == "McpTool" && x.TargetId == key.Key.Id.ToString() && x.Action == "MCP search_tags");
            var denied = await _site.LastLogAsync(x => x.TargetType == "McpTool" && x.TargetId == key.Key.Id.ToString() && x.Action == "MCP query_alarms");
            Assert.NotNull(success);
            Assert.Equal("成功", success.Result);
            Assert.NotNull(denied);
            Assert.StartsWith("拒绝", denied.Result);
            Assert.DoesNotContain(key.Secret, JsonSerializer.Serialize(new[] { success, denied }));
        }
        var (writeOnly, _) = await KeyClientAsync(_site, ExternalApiScope.Write);
        using (writeOnly)
            Assert.True((await CallAsync(writeOnly, "search_tags")).GetProperty("isError").GetBoolean());
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
        var (client, _) = await KeyClientAsync(_site, ExternalApiScope.All);
        using (client)
        using (var json = JsonDocument.Parse(arguments))
        {
            var result = await CallAsync(client, tool, json.RootElement);
            Assert.True(result.GetProperty("isError").GetBoolean(), result.ToString());
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
        var (client, _) = await KeyClientAsync(_site, ExternalApiScope.ReadHistory, allAreas: true);
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
    public async Task Alarm_Visibility_Is_Applied_Before_Paging_And_History_Is_ReadOnly()
    {
        var now = ScadaTime.NowMs();
        var factory = _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.AlarmEvents.AddRange(Enumerable.Range(1, 150).Select(i => new AlarmEvent
        {
            TagId = _site.TagB, DeviceId = _site.DeviceB, Message = "hidden", StartTs = now + i
        }));
        var historical = new AlarmEvent { TagId = _site.TagA, DeviceId = _site.DeviceA, Message = "returned", StartTs = now - 10000, EndTs = now - 5000 };
        db.AlarmEvents.Add(historical);
        await db.SaveChangesAsync();
        var (client, _) = await KeyClientAsync(_site, ExternalApiScope.ReadAlarms);
        using (client)
        {
            var active = Data(await CallAsync(client, "query_alarms", new { limit = 1 })).GetProperty("items");
            Assert.Equal(_site.AlarmA, Assert.Single(active.EnumerateArray()).GetProperty("id").GetInt32());
            var first = Data(await CallAsync(client, "query_alarms", new { activeOnly = false, from = now - 20000, to = now + 200, limit = 1 }));
            Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
            var second = Data(await CallAsync(client, "query_alarms", new { activeOnly = false, from = now - 20000, to = now + 200, offset = 1, limit = 1 }));
            Assert.Equal(historical.Id, second.GetProperty("items")[0].GetProperty("id").GetInt32());
            Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
            Assert.Null((await db.AlarmEvents.AsNoTracking().SingleAsync(x => x.Id == _site.AlarmA)).AckTs);
        }
    }

    [Fact]
    public async Task Tag_Paging_And_Request_Limits_Are_Enforced()
    {
        var (client, _) = await KeyClientAsync(_site, ExternalApiScope.ReadTags, allAreas: true);
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
        var (limited, _) = await KeyClientAsync(_site, ExternalApiScope.ReadTags, rate: 1);
        using (limited)
        {
            await RpcAsync(limited, "tools/list");
            using var response = await SendAsync(limited, "tools/list");
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
        }
    }
}

public sealed class McpDisabledTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public McpDisabledTests(AuthorizationTests.Site site) => _site = site;

    [Fact]
    public async Task Disabled_By_Default_Returns_NotFound_While_Existing_Apis_Work()
    {
        using var client = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.NotFound, (await McpApiTests.SendAsync(client, "tools/list")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(McpEndpoints.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tags")).StatusCode);
    }
}

public sealed class McpJwtTests : IClassFixture<McpJwtTests.Site>
{
    private readonly Site _site;
    public McpJwtTests(Site site) => _site = site;

    public sealed class Site : McpApiTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder builder)
        {
            base.ConfigureSettings(builder);
            builder.UseSetting("Mcp:AllowJwt", "true");
        }
    }

    [Fact]
    public async Task OptIn_Jwt_Uses_Current_Area_Permissions_On_Every_Request()
    {
        using var client = await _site.ClientAsync("watchA");
        var data = McpApiTests.Data(await McpApiTests.CallAsync(client, "search_tags"));
        Assert.Contains(data.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetInt32() == _site.TagA);
        Assert.DoesNotContain(data.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetInt32() == _site.TagB);
        using var admin = await _site.ClientAsync("admin");
        var response = await admin.PutAsJsonAsync($"/api/users/{_site.WatchUserId}/assignments", new { assignments = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var denied = await McpApiTests.CallAsync(client, "search_tags");
        Assert.True(denied.GetProperty("isError").GetBoolean());
    }
}
