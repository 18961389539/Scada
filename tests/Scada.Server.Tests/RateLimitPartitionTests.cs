using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Scada.Server.Startup;

namespace Scada.Server.Tests;

public sealed class RateLimitPartitionTests
{
    [Theory]
    [InlineData("/api/ha/role", true)]
    [InlineData("/api/ha/peer/status", true)]
    [InlineData("/health/live", true)]
    [InlineData("/assets/index.js", true)]
    [InlineData("/api/alarms/active", false)]
    [InlineData("/api/desktop/heartbeat", false)]
    [InlineData("/api/users", false)]
    public void OnlyClusterStatusAndStaticAssetsBypassQuota(string path, bool expected) =>
        Assert.Equal(expected, ScadaResilience.BypassesRateLimit(new PathString(path)));

    /// <summary>
    /// 控制室里几十台操作台常用同一个只读账号自动登录。只按 userId 分区时它们的
    /// 后台轮询会合用一个 1200/min 的桶；按「身份 + 终端地址」分区后各用各的。
    /// </summary>
    [Fact]
    public void SameAccountOnDifferentTerminalsUsesDifferentBuckets()
    {
        var first = Authenticated(userId: 7, "10.1.0.11");
        var second = Authenticated(userId: 7, "10.1.0.12");

        var firstKey = Key(first);
        var secondKey = Key(second);

        Assert.Equal("user:7:ip:10.1.0.11", firstKey);
        Assert.NotEqual(firstKey, secondKey);
    }

    /// <summary>同一台机器开多个标签页仍然共享一个桶，脚本刷接口的放大幅度不变。</summary>
    [Fact]
    public void TabsOnSameTerminalShareOneBucket()
    {
        Assert.Equal(
            Key(Authenticated(7, "10.1.0.11")),
            Key(Authenticated(7, "10.1.0.11")));
    }

    /// <summary>拿不到来源地址时（TestServer、转发）退回账号级桶，语义与只按账号分区一致。</summary>
    [Fact]
    public void MissingRemoteAddressFallsBackToAccountBucket()
    {
        Assert.Equal("user:7:ip:local", Key(Authenticated(7, address: null)));
    }

    /// <summary>API 密钥与网关凭据在限流阶段还没进 http.User，靠请求头指纹单独分区。</summary>
    [Fact]
    public void ApiKeyAndGatewayCredentialsGetTheirOwnBuckets()
    {
        var apiKey = new DefaultHttpContext();
        apiKey.Request.Headers["X-Api-Key"] = "key-1";
        var gateway = new DefaultHttpContext();
        gateway.Request.Headers.Authorization = "Gateway gateway-secret";

        var apiIdentity = ScadaResilience.CredentialPartitionKey(apiKey);
        var gatewayIdentity = ScadaResilience.CredentialPartitionKey(gateway);

        Assert.StartsWith("apikey:", apiIdentity);
        Assert.StartsWith("gateway:", gatewayIdentity);
        Assert.NotEqual(apiIdentity, gatewayIdentity);
        Assert.Null(ScadaResilience.CredentialPartitionKey(new DefaultHttpContext()));
    }

    private static string Key(HttpContext context) =>
        ScadaResilience.PartitionKey(context, ScadaResilience.CredentialPartitionKey(context)!);

    private static DefaultHttpContext Authenticated(int userId, string? address)
    {
        var context = new DefaultHttpContext();
        if (address is not null) context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture))],
            authenticationType: "test"));
        return context;
    }
}
