using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Server.Tests;

/// <summary>
/// 面向大模型的只读 API（/api/ai/*）端到端验证。
/// 关注三件事：只读保证、区域隔离、鉴权范围。
/// </summary>
public sealed class AiApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public AiApiTests(AuthorizationTests.Site site) => _site = site;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<(HttpClient Client, int KeyId)> CreateKeyAsync(params string[] scopes)
    {
        var admin = await _site.ClientAsync("admin");
        var created = await Json(await admin.PostAsJsonAsync("/api/external/keys", new
        {
            name = "ai-" + Guid.NewGuid().ToString("N")[..8],
            scopes,
            areaIds = new[] { _site.AreaA },
            rateLimitPerMinute = 1000,
            notes = "ai test"
        }));
        var secret = created.GetProperty("secret").GetString()!;
        var id = created.GetProperty("key").GetProperty("id").GetInt32();
        var client = _site.Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return (client, id);
    }

    private async Task RevokeAsync(int keyId)
    {
        var admin = await _site.ClientAsync("admin");
        await Json(await admin.PostAsJsonAsync($"/api/external/keys/{keyId}/revoke", new { }));
    }

    [Fact]
    public async Task ToolManifest_IsAnonymous_AndDescribesEveryReadTool()
    {
        var anon = _site.Server.CreateClient();
        var manifest = await Json(await anon.GetAsync("/api/ai/tools"));
        Assert.True(manifest.GetProperty("readOnly").GetBoolean());
        var tools = manifest.GetProperty("tools").EnumerateArray().ToArray();
        var names = tools.Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToArray();
        // 每个工具都要带可用的参数 schema，模型才能正确生成调用参数。
        Assert.Contains("scada_list_devices", names);
        Assert.Contains("scada_get_realtime", names);
        Assert.Contains("scada_get_trends", names);
        Assert.Contains("scada_query_alarms", names);
        Assert.Contains("scada_get_reports", names);
        Assert.Contains("scada_list_tags", names);
        Assert.All(tools, t => Assert.Equal("function", t.GetProperty("type").GetString()));
        Assert.All(tools, t => Assert.Equal("object", t.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString()));
    }

    [Fact]
    public async Task ReadEndpoints_ReturnData_ForAuthorizedArea()
    {
        var (client, keyId) = await CreateKeyAsync("ReadTags", "ReadHistory", "ReadAlarms");

        var devices = await Json(await client.GetAsync("/api/ai/devices"));
        var deviceItems = devices.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(deviceItems, d => d.GetProperty("id").GetInt32() == _site.DeviceA);
        // B 区设备对 A 区密钥不可见：整个对象都不出现，而不是标记为不可见。
        Assert.DoesNotContain(deviceItems, d => d.GetProperty("id").GetInt32() == _site.DeviceB);
        Assert.True(devices.GetProperty("dataAsOf").GetInt64() > 0);

        var tags = await Json(await client.GetAsync("/api/ai/tags"));
        var tagItems = tags.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(tagItems, t => t.GetProperty("id").GetInt32() == _site.TagA);
        Assert.DoesNotContain(tagItems, t => t.GetProperty("id").GetInt32() == _site.TagB);

        var cache = _site.Services.GetRequiredService<IRealtimeCache>();
        cache.Set(new TagValue(_site.TagA, 21.5, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));
        var realtime = await Json(await client.GetAsync($"/api/ai/realtime?tagIds={_site.TagA}"));
        var items = realtime.GetProperty("items").EnumerateArray().ToArray();
        Assert.Single(items);
        Assert.Equal(21.5, items[0].GetProperty("value").GetDouble(), 3);
        Assert.Equal("Good", items[0].GetProperty("quality").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/ai/trends?tagIds={_site.TagA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/ai/reports?tagIds={_site.TagA}&type=daily")).StatusCode);

        var alarms = await Json(await client.GetAsync("/api/ai/alarms?limit=50"));
        var alarmItems = alarms.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(alarmItems, a => a.GetProperty("id").GetInt32() == _site.AlarmA);
        Assert.DoesNotContain(alarmItems, a => a.GetProperty("id").GetInt32() == _site.AlarmB);
        Assert.True(alarms.GetProperty("total").GetInt32() >= 1);

        await RevokeAsync(keyId);
    }

    [Fact]
    public async Task CrossAreaTags_AreRejected_NotSilentlyDropped()
    {
        var (client, keyId) = await CreateKeyAsync("ReadTags", "ReadHistory", "ReadAlarms");

        // 显式请求越权测点：整体拒绝，不能返回部分结果让模型误判。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/ai/realtime?tagIds={_site.TagB}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/ai/trends?tagIds={_site.TagA},{_site.TagB}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/ai/reports?tagIds={_site.TagB}")).StatusCode);

        await RevokeAsync(keyId);
    }

    [Fact]
    public async Task MissingScope_IsForbidden_AndJwtWorks()
    {
        var (client, keyId) = await CreateKeyAsync("ReadTags");
        // 只有 ReadTags，读历史与报警应被拒绝。
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/ai/trends?tagIds={_site.TagA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/ai/alarms?limit=5")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/ai/reports?tagIds={_site.TagA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/ai/devices")).StatusCode);

        // JWT 用户按自身权限映射范围。
        var jwt = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.OK, (await jwt.GetAsync("/api/ai/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await jwt.GetAsync("/api/ai/alarms?limit=5")).StatusCode);

        await RevokeAsync(keyId);
    }

    [Fact]
    public async Task AiGroup_IsReadOnly_NoWriteEndpointExists()
    {
        var (client, keyId) = await CreateKeyAsync("ReadTags", "ReadHistory", "ReadAlarms", "Write");
        var endpoints = _site.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText?.StartsWith("/api/ai/", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint =>
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
            Assert.NotNull(methods);
            Assert.Equal(new[] { "GET" }, methods.HttpMethods);
        });
        Assert.DoesNotContain(endpoints, x => x.RoutePattern.RawText == "/api/ai/commands");

        // Unmatched methods use fallback JWT authentication, which can return 401 before routing returns 405/404.
        foreach (var path in new[] { "/api/ai/devices", "/api/ai/realtime", "/api/ai/trends", "/api/ai/alarms", "/api/ai/reports", "/api/ai/tags" })
        {
            var post = await client.PostAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.True(post.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized,
                $"{path} POST 应当被拒绝，实际 {(int)post.StatusCode}");
        }

        var command = await client.PostAsync("/api/ai/commands",
            new StringContent($"{{\"tagId\":{_site.TagA},\"value\":1}}", System.Text.Encoding.UTF8, "application/json"));
        Assert.True(command.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized,
            $"/api/ai/commands POST 应当被拒绝，实际 {(int)command.StatusCode}");

        await RevokeAsync(keyId);
    }

    [Fact]
    public async Task Unauthenticated_IsRejected_AndRevokedKeyStopsWorking()
    {
        var anon = _site.Server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/ai/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/ai/tags")).StatusCode);

        var (client, keyId) = await CreateKeyAsync("ReadTags");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/ai/devices")).StatusCode);
        await RevokeAsync(keyId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/ai/devices")).StatusCode);
    }

    [Fact]
    public async Task AiCalls_AreAudited()
    {
        var (client, keyId) = await CreateKeyAsync("ReadTags");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/ai/devices")).StatusCode);
        var log = await _site.LastLogAsync(x => x.TargetType == "AiApi" && x.Action == "GET /api/ai/devices");
        Assert.NotNull(log);
        await RevokeAsync(keyId);
    }

    [Fact]
    public async Task OpenApiDocument_ListsAiEndpoints()
    {
        var anon = _site.Server.CreateClient();
        var response = await anon.GetAsync("/openapi/ai.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadAsStringAsync();
        Assert.Contains("/api/ai/devices", doc);
        Assert.Contains("/api/ai/trends", doc);
        Assert.Contains("/api/ai/alarms", doc);
    }

    [Fact]
    public async Task InvalidParameters_ReturnBadRequest()
    {
        var (client, keyId) = await CreateKeyAsync("ReadTags", "ReadHistory", "ReadAlarms");
        // 趋势不指定测点：必须明确报错，不能返回空结果冒充“没有数据”。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/ai/trends")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/ai/reports")).StatusCode);
        // 无效的状态筛选值。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/ai/alarms?state=NotAState")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/ai/alarms?level=NotALevel")).StatusCode);
        // 结束早于开始。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/ai/trends?tagIds={_site.TagA}&from=2000&to=1000")).StatusCode);
        await RevokeAsync(keyId);
    }
}
