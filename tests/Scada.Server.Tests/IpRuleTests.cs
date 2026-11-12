using System.Net;
using Scada.Core.Net;

namespace Scada.Server.Tests;

public sealed class IpRuleTests
{
    [Theory]
    [InlineData("10.20.0.0/24", "10.20.0.5", true)]
    [InlineData("10.20.0.0/24", "10.20.1.5", false)]
    [InlineData("192.168.1.7", "192.168.1.7", true)]
    [InlineData("192.168.1.7", "192.168.1.8", false)]
    [InlineData("*", "203.0.113.9", true)]
    [InlineData("::1/128", "::1", true)]
    [InlineData("10.0.0.0/8", "::ffff:10.1.2.3", true)]
    [InlineData("10.0.0.0/8", "::1", false)]
    public void Matches_cidr_exact_and_any(string rule, string address, bool expected) =>
        Assert.Equal(expected, IpRule.IsMatch(IPAddress.Parse(address), rule));

    [Fact]
    public void Empty_allowlist_denies_by_default() =>
        Assert.False(IpRule.IsAllowed(IPAddress.Loopback, []));

    [Fact]
    public void Null_address_is_denied() =>
        Assert.False(IpRule.IsAllowed(null, ["*"]));

    [Theory]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("10.0.0.0/-1", false)]
    [InlineData("localhost", false)]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("*", true)]
    public void Validates_rule_syntax(string rule, bool expected) =>
        Assert.Equal(expected, IpRule.IsValid(rule));
}
