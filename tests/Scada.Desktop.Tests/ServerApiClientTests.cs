using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Scada.Desktop.Api;

namespace Scada.Desktop.Tests;

public sealed class ServerApiClientTests
{
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
