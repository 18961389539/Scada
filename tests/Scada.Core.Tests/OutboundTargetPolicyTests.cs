using System.Net;
using Scada.Core.Net;
using Scada.Core.Options;
using Scada.Core.Protocols;

namespace Scada.Core.Tests;

public class OutboundTargetPolicyTests
{
    [Theory]
    [InlineData("10.1.2.3", "10.0.0.0/8")]
    [InlineData("192.168.1.20", "192.168.1.0/24")]
    [InlineData("::ffff:10.1.2.3", "10.0.0.0/8")]
    [InlineData("2001:db8::1", "2001:db8::/32")]
    public void AllowlistedTargetIsAcceptedByBothEntryPoints(string host, string rule)
    {
        // 两套 CIDR 实现曾经各写一遍，边界处理已经分叉。现在 OutboundTargetPolicy 直接用 IpRule，
        // 这条用例把两个入口钉在一起：任何一边改坏都会红。
        Assert.True(IpRule.IsMatch(IPAddress.Parse(host), rule));
        Assert.Null(OutboundTargetPolicy.Validate(host, new ScadaSettings { CollectorTargetAllowlist = [rule] }));
    }

    [Theory]
    [InlineData("198.51.100.7", "203.0.113.0/24")]
    [InlineData("2001:db9::1", "2001:db8::/32")]
    public void PublicTargetOutsideAllowlistIsRejectedByBoth(string host, string rule)
    {
        Assert.False(IpRule.IsMatch(IPAddress.Parse(host), rule));
        Assert.NotNull(OutboundTargetPolicy.Validate(host, new ScadaSettings { CollectorTargetAllowlist = [rule] }));
    }

    [Fact]
    public void AllowlistIsAdditiveForPrivateAddresses()
    {
        // 语义是"额外放行"而不是"仅允许"：私网目标本来就默认允许，白名单只用来补充公网/主机名例外。
        Assert.Null(OutboundTargetPolicy.Validate("192.168.2.20", new ScadaSettings { CollectorTargetAllowlist = ["192.168.1.0/24"] }));
    }

    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("a.b.example.com", true)]
    [InlineData("example.com", false)]
    [InlineData("evilexample.com", false)]
    public void WildcardAllowsSubdomainsOnly(string host, bool allowed)
    {
        var settings = new ScadaSettings { CollectorTargetAllowlist = ["*.example.com"] };

        Assert.Equal(allowed, OutboundTargetPolicy.Validate(host, settings) is null);
    }

    [Fact]
    public void PublicAddressIsRejectedByDefaultButLoopbackIsNot()
    {
        Assert.Null(OutboundTargetPolicy.Validate("127.0.0.1", new ScadaSettings()));
        Assert.Null(OutboundTargetPolicy.Validate("192.168.10.5", new ScadaSettings()));
        var error = OutboundTargetPolicy.Validate("203.0.113.9", new ScadaSettings());
        Assert.NotNull(error);
        Assert.Contains("公网", error);
        Assert.Null(OutboundTargetPolicy.Validate("203.0.113.9", new ScadaSettings { AllowPublicCollectorTargets = true }));
    }

    [Fact]
    public void HostnameRequiresAllowlistOnlyWhenConfigured()
    {
        // 未配置白名单时放行主机名（现场普遍用内网 DNS）；配置后切到严格模式。
        Assert.Null(OutboundTargetPolicy.Validate("plc-a.plant.local", new ScadaSettings()));
        Assert.NotNull(OutboundTargetPolicy.Validate("plc-a.plant.local", new ScadaSettings { CollectorTargetAllowlist = ["10.0.0.0/8"] }));
    }

    [Fact]
    public void IpRuleEmptyListDeniesEverything()
    {
        Assert.False(IpRule.IsAllowed(IPAddress.Loopback, []));
        Assert.False(IpRule.IsAllowed(null, ["*"]));
        Assert.True(IpRule.IsAllowed(IPAddress.Loopback, ["*"]));
    }
}
