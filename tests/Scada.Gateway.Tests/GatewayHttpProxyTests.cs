using System.Net;
using Scada.Gateway;

namespace Scada.Gateway.Tests;

public class GatewayHttpProxyTests
{
    private sealed class RecordingProxy(Uri? target) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public int Lookups;
        public Uri? GetProxy(Uri destination) { Lookups++; return target; }
        public bool IsBypassed(Uri host) => false;
    }

    // 系统代理按主机名复用上游连接、忽略端口：回环请求一旦交给代理，主备两个端口的流量会
    // 串到同一个套接字上（AgentRotatesFromStandbyCenterToPrimary 就是被这条踩住的）。
    [Theory]
    [InlineData("http://127.0.0.1:5080/")]
    [InlineData("http://127.0.0.1:5081/api/gateway-agent/hello")]
    [InlineData("http://localhost:5080/")]
    [InlineData("https://[::1]:5443/api/gateway-agent/hello")]
    public void LoopbackTargetsConnectDirectlyWithoutConsultingTheProxy(string url)
    {
        var fallback = new RecordingProxy(new Uri("http://proxy.example:8080"));
        var proxy = new LoopbackBypassProxy(fallback);
        var target = new Uri(url);
        Assert.True(proxy.IsBypassed(target));
        Assert.Equal(target, proxy.GetProxy(target));
        Assert.Equal(0, fallback.Lookups);
    }

    [Fact]
    public void RemoteTargetsKeepUsingTheSystemProxy()
    {
        var system = new Uri("http://proxy.example:8080");
        var fallback = new RecordingProxy(system);
        var proxy = new LoopbackBypassProxy(fallback);
        var target = new Uri("https://center.example.com/api/gateway-agent/hello");
        Assert.False(proxy.IsBypassed(target));
        Assert.Equal(system, proxy.GetProxy(target));
        Assert.Equal(1, fallback.Lookups);
    }

    [Fact]
    public void MissingSystemProxyMeansEveryTargetIsDirect()
    {
        var proxy = new LoopbackBypassProxy();
        var target = new Uri("https://center.example.com/api/gateway-agent/hello");
        Assert.True(proxy.IsBypassed(target));
        Assert.Equal(target, proxy.GetProxy(target));
    }

    [Fact]
    public void CredentialsAreForwardedToTheSystemProxy()
    {
        var fallback = new RecordingProxy(null); var credentials = new NetworkCredential("user", "secret");
        var proxy = new LoopbackBypassProxy(fallback) { Credentials = credentials };
        Assert.Same(credentials, fallback.Credentials);
        Assert.Same(credentials, proxy.Credentials);
    }

    [Fact]
    public void GatewayHandlerShipsWithTheLoopbackBypass()
    {
        using var handler = GatewayHttp.CreateHandler();
        var proxy = Assert.IsType<LoopbackBypassProxy>(handler.Proxy);
        Assert.True(proxy.IsBypassed(new Uri("http://127.0.0.1:5081/")));
    }
}
