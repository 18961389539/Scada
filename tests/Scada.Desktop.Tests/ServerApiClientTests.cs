using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Scada.Desktop.Api;

namespace Scada.Desktop.Tests;

public sealed class ServerApiClientTests
{
    /// <summary>
    /// 终端只连内网服务端；默认 handler 若读取系统代理，企业镜像里会出现
    /// "浏览器能打开、客户端连不上"。这条守住 CreateHandler 的 UseProxy=false。
    /// </summary>
    [Fact]
    public void DefaultHandlerBypassesSystemProxy()
    {
        using var handler = ServerApiClient.CreateHandler();
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task ReadsSiteInfo()
    {
        using var client = Client(_ => Json(HttpStatusCode.OK, """{"siteName":"测试站点","demoMode":true}"""));
        var info = await client.GetSiteInfoAsync();
        Assert.Equal("测试站点", info.SiteName);
        Assert.True(info.DemoMode);
    }

    [Fact]
    public async Task ParsesSuccessfulLogin()
    {
        using var client = Client(_ => Json(HttpStatusCode.OK,
            """{"token":"t1","expiresAt":"2026-11-15T20:00:00+08:00","user":{"id":1,"userName":"kiosk01","mustChangePassword":false,"locale":"zh-CN"}}"""));

        var result = await client.LoginAsync("kiosk01", "secret");

        Assert.Equal(LoginStatus.Success, result.Status);
        Assert.NotNull(result.Session);
        Assert.Equal("t1", result.Session!.Token);
        Assert.Contains("kiosk01", result.Session.UserJson);
        Assert.Equal("zh-CN", result.Session.Locale);
        Assert.False(result.Session.MustChangePassword);
    }

    [Fact]
    public async Task FlagsMustChangePassword()
    {
        using var client = Client(_ => Json(HttpStatusCode.OK,
            """{"token":"t1","expiresAt":"2026-11-15T20:00:00+08:00","user":{"id":1,"mustChangePassword":true}}"""));

        var result = await client.LoginAsync("kiosk01", "secret");
        Assert.Equal(LoginStatus.MustChangePassword, result.Status);
        Assert.NotNull(result.Session);
        Assert.Equal("t1", result.Session!.Token);
        Assert.True(result.Session.MustChangePassword);
        Assert.Equal(LoginResult.MustChangePasswordMessage, result.Message);
    }

    [Fact]
    public async Task ChangePasswordReturnsNewSession()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        using var client = Client(request =>
        {
            captured = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK,
                """{"token":"t2","expiresAt":"2026-11-15T20:00:00+08:00","user":{"id":1,"userName":"admin","mustChangePassword":false}}""");
        });

        var result = await client.ChangePasswordAsync("t1", "admin123", "newpass");
        Assert.Equal(LoginStatus.Success, result.Status);
        Assert.Equal("t2", result.Session!.Token);
        Assert.False(result.Session.MustChangePassword);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/api/auth/password", captured.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("t1", captured.Headers.Authorization.Parameter);
        Assert.Contains("admin123", body);
        Assert.Contains("newpass", body);
    }

    [Fact]
    public async Task ChangePasswordMapsValidationMessage()
    {
        using var client = Client(_ => Json(HttpStatusCode.BadRequest, """{"message":"新密码不能与当前密码相同"}"""));
        var result = await client.ChangePasswordAsync("t1", "admin123", "admin123");
        Assert.Equal(LoginStatus.Protocol, result.Status);
        Assert.Equal("新密码不能与当前密码相同", result.Message);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task MapsUnauthorizedLogin()
    {
        using var client = Client(_ => Json(HttpStatusCode.Unauthorized, """{"message":"用户名或密码错误"}"""));
        var result = await client.LoginAsync("kiosk01", "wrong");
        Assert.Equal(LoginStatus.InvalidCredentials, result.Status);
        Assert.Equal("用户名或密码错误", result.Message);
    }

    [Fact]
    public async Task MapsRateLimitWithRetryAfter()
    {
        using var client = Client(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"message":"登录失败次数过多"}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        var result = await client.LoginAsync("kiosk01", "secret");
        Assert.Equal(LoginStatus.RateLimited, result.Status);
        Assert.NotNull(result.RetryAfter);
        Assert.Equal(30, (int)result.RetryAfter!.Value.TotalSeconds);
    }

    [Fact]
    public async Task MapsServerError()
    {
        using var client = Client(_ => Json(HttpStatusCode.InternalServerError, """{"message":"boom"}"""));
        var result = await client.LoginAsync("kiosk01", "secret");
        Assert.Equal(LoginStatus.Server, result.Status);
    }

    [Fact]
    public async Task MapsNetworkFailure()
    {
        using var client = Client(_ => throw new HttpRequestException("boom", new SocketException((int)SocketError.HostNotFound)));
        var result = await client.LoginAsync("kiosk01", "secret");
        Assert.Equal(LoginStatus.Network, result.Status);
        Assert.Contains("找不到服务器", result.Message);
    }

    [Fact]
    public async Task FiltersUnpublishedScreens()
    {
        using var client = Client(_ => Json(HttpStatusCode.OK,
            """[{"id":1,"name":"已发布","publishedRevision":3},{"id":2,"name":"草稿","publishedRevision":null}]"""));

        var screens = await client.GetScreensAsync("token");
        Assert.Single(screens);
        Assert.Equal(1, screens[0].Id);
    }

    [Fact]
    public async Task ReportsUnauthorizedListRequest()
    {
        using var client = Client(_ => Json(HttpStatusCode.Unauthorized, """{"message":"unauthorized"}"""));
        var exception = await Assert.ThrowsAsync<ApiException>(() => client.GetScreensAsync("expired"));
        Assert.Equal(401, exception.StatusCode);
        Assert.Contains("登录会话已失效", exception.Message);
    }

    [Fact]
    public async Task SendsHeartbeatAndParsesPolicy()
    {
        HttpRequestMessage? captured = null;
        using var client = Client(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK,
                """{"ok":true,"serverTs":1,"node":"A","haRole":"Primary","policy":{"minimumVersion":"1.0.0","latestVersion":"1.2.0","downloadUrl":"https://example.com/a.zip","updateRequired":false,"updateAvailable":true},"heartbeatSeconds":30}""");
        });

        var result = await client.HeartbeatAsync("token", new HeartbeatPayload(
            Guid.NewGuid().ToString("D"), "PC", "operator", "1.1.0", "131.0", "boot", "screen", 1, "总览", "machine", null));

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/api/desktop/heartbeat", captured.RequestUri!.AbsolutePath);
        Assert.True(result.Ok);
        Assert.Equal("A", result.Node);
        Assert.True(result.Policy!.UpdateAvailable);
        Assert.Equal(30, result.HeartbeatSeconds);
    }

    [Fact]
    public async Task Heartbeat503ExposesPrimaryUrl()
    {
        using var client = Client(_ =>
        {
            var response = Json(HttpStatusCode.ServiceUnavailable, """{"message":"standby","primaryUrl":"http://primary:5080"}""");
            response.Headers.Add("X-Scada-Primary", "http://primary:5080");
            return response;
        });

        var exception = await Assert.ThrowsAsync<ApiException>(() => client.HeartbeatAsync(
            "token", new HeartbeatPayload(Guid.NewGuid().ToString("D"), "PC", "operator", "1.1.0", null, null, "screen", 1, null, "user", null)));
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("http://primary:5080", exception.PrimaryUrl);
    }

    [Fact]
    public async Task ReadsHaRole()
    {
        using var client = Client(_ => Json(HttpStatusCode.OK,
            """{"enabled":true,"node":"B","role":"Standby","primaryUrl":"http://primary:5080"}"""));
        var role = await client.GetHaRoleAsync();
        Assert.NotNull(role);
        Assert.True(role!.Enabled);
        Assert.Equal("Standby", role.Role);
        Assert.Equal("http://primary:5080", role.PrimaryUrl);
    }

    [Fact]
    public async Task List503ExposesPrimaryUrl()
    {
        using var client = Client(_ =>
        {
            var response = Json(HttpStatusCode.ServiceUnavailable, """{"message":"standby"}""");
            response.Headers.Add("X-Scada-Primary", "http://primary:5080");
            return response;
        });

        var exception = await Assert.ThrowsAsync<ApiException>(() => client.GetScreensAsync("token"));
        Assert.Equal("http://primary:5080", exception.PrimaryUrl);
    }

    private static ServerApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new("http://unit.test:5080", new StubHandler(responder));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
