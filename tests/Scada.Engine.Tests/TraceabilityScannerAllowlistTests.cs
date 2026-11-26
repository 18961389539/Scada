using System.Net;
using Scada.Engine.Traceability;

namespace Scada.Engine.Tests;

public sealed class TraceabilityScannerAllowlistTests
{
    [Fact]
    public void LoopbackListenRecognizesIpv4AndIpv6()
    {
        Assert.True(TraceabilityScannerConnection.IsLoopbackListen("127.0.0.1"));
        Assert.True(TraceabilityScannerConnection.IsLoopbackListen("::1"));
        Assert.False(TraceabilityScannerConnection.IsLoopbackListen("0.0.0.0"));
        Assert.False(TraceabilityScannerConnection.IsLoopbackListen("10.0.0.1"));
    }

    [Fact]
    public void EmptyAllowlistAllowsAnySource()
    {
        Assert.True(TraceabilityScannerConnection.IsAllowedSource(IPAddress.Parse("8.8.8.8"), []));
        Assert.True(TraceabilityScannerConnection.IsAllowedSource(IPAddress.Parse("8.8.8.8"), null));
    }

    [Fact]
    public void CidrAndExactAddressAreEnforced()
    {
        var allow = new[] { "10.0.0.0/24", "192.168.1.10" };
        Assert.True(TraceabilityScannerConnection.IsAllowedSource(IPAddress.Parse("10.0.0.20"), allow));
        Assert.True(TraceabilityScannerConnection.IsAllowedSource(IPAddress.Parse("192.168.1.10"), allow));
        Assert.False(TraceabilityScannerConnection.IsAllowedSource(IPAddress.Parse("10.0.1.20"), allow));
        Assert.False(TraceabilityScannerConnection.IsAllowedSource(IPAddress.Parse("192.168.1.11"), allow));
    }

    [Fact]
    public void InvalidAllowedSourcesAreReported()
    {
        Assert.Null(TraceabilityScannerConnection.InvalidAllowedSources(["10.0.0.1", "10.1.0.0/16"]));
        Assert.Contains("不是有效", TraceabilityScannerConnection.InvalidAllowedSources(["10.0.0.1/99"])!);
    }
}
