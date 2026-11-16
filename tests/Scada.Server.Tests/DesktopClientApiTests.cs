using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Scada.Server.Tests;

public sealed class DesktopClientApiTests(AuthorizationTests.Site site) : IClassFixture<AuthorizationTests.Site>
{
    /// <summary>
    /// 体积上限必须在读请求体之前生效。放在处理器里看 ContentLength 是模型绑定之后的事，
    /// chunked 请求连长度都没有；只有端点元数据会被 Kestrel 读取（TestServer 不执行这层）。
    /// </summary>
    [Fact]
    public void HeartbeatDeclaresRequestSizeLimitBeforeBinding()
    {
        var endpoints = site.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var endpoint = Assert.Single(
            endpoints.OfType<RouteEndpoint>(),
            item => item.RoutePattern.RawText == "/api/desktop/heartbeat");
        var limit = endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();

        Assert.NotNull(limit);
        Assert.Equal(4096, limit!.MaxRequestBodySize);
    }

    [Fact]
    public async Task AnonymousHeartbeatIsRejected()
    {
        using var anonymous = site.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/desktop/heartbeat", Payload(Guid.NewGuid().ToString("D"), "PC-A"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ViewerCanHeartbeatButCannotReadClientList()
    {
        using var viewer = await site.ClientAsync("opA");
        var clientId = Guid.NewGuid().ToString("D");
        var heartbeat = await viewer.PostAsJsonAsync("/api/desktop/heartbeat", Payload(clientId, "PC-A"));
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        var body = await heartbeat.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(30, body.GetProperty("heartbeatSeconds").GetInt32());

        var denied = await viewer.GetAsync("/api/desktop/clients");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task AdminSeesRegisteredClientsAndDuplicateDoesNotCreateRows()
    {
        using var operatorClient = await site.ClientAsync("opA");
        using var admin = await site.ClientAsync("admin");
        var clientId = Guid.NewGuid().ToString("D");
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.PostAsJsonAsync("/api/desktop/heartbeat", Payload(clientId, "PC-A"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.PostAsJsonAsync("/api/desktop/heartbeat", Payload(clientId, "PC-B"))).StatusCode);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/desktop/clients");
        var client = Assert.Single(
            list.GetProperty("clients").EnumerateArray(),
            item => item.GetProperty("clientId").GetString() == clientId);
        Assert.Equal(clientId, client.GetProperty("clientId").GetString());
        Assert.True(client.GetProperty("conflict").GetBoolean());
        Assert.Equal("PC-A", client.GetProperty("previousMachineName").GetString());
        Assert.True(client.GetProperty("online").GetBoolean());
        Assert.Equal("Standalone", list.GetProperty("haRole").GetString());
    }

    [Fact]
    public async Task InvalidClientIdIsRejected()
    {
        using var viewer = await site.ClientAsync("opA");
        var response = await viewer.PostAsJsonAsync("/api/desktop/heartbeat", Payload("bad-id", "PC-A"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static object Payload(string clientId, string machine) => new
    {
        clientId,
        machineName = machine,
        windowsUser = "operator",
        appVersion = "1.0.0",
        webView2Version = "131.0",
        bootId = "boot",
        targetKind = "screen",
        targetId = 1,
        targetName = "总览",
        configScope = "user",
        blockedReason = (string?)null
    };
}


