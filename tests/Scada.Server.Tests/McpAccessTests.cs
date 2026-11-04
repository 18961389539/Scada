using System.Net;
using Microsoft.AspNetCore.Hosting;
using Scada.Server.Mcp;

namespace Scada.Server.Tests;

/// <summary>
/// 交付默认姿态：MCP 端点只允许本机来源，且不注册写工具。
/// 这两条是“匿名端点 + 写能力”组合的边界，回退会直接放开现场风险，因此单独锁死。
/// </summary>
public sealed class McpAccessTests : IClassFixture<McpAccessTests.RestrictedSite>
{
    public sealed class RestrictedSite : McpApiTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder builder)
        {
            base.ConfigureSettings(builder);
            // 覆盖回交付默认：任何来源可连（测试服务器没有来源地址），但写工具一律不注册。
            builder.UseSetting("Mcp:AllowControlWrites", "false");
            builder.UseSetting("Mcp:AllowConfigurationWrites", "false");
        }
    }

    private readonly RestrictedSite _site;
    public McpAccessTests(RestrictedSite site) => _site = site;

    [Fact]
    public void Options_Default_To_Loopback_And_No_Write_Tools()
    {
        var options = new McpOptions();
        Assert.True(options.Enabled);
        Assert.False(options.AllowControlWrites);
        Assert.False(options.AllowConfigurationWrites);
        Assert.Empty(options.AllowedCidrs);
    }

    [Fact]
    public void Source_Guard_Defaults_To_Loopback()
    {
        Assert.True(McpSourceGuard.IsAllowed(null, []));
        Assert.True(McpSourceGuard.IsAllowed(IPAddress.Loopback, []));
        Assert.True(McpSourceGuard.IsAllowed(IPAddress.IPv6Loopback, []));
        Assert.False(McpSourceGuard.IsAllowed(IPAddress.Parse("10.1.2.3"), []));
        Assert.False(McpSourceGuard.IsAllowed(IPAddress.Parse("192.168.1.10"), []));
    }

    [Theory]
    [InlineData("10.1.2.3", "10.0.0.0/8", true)]
    [InlineData("10.1.2.3", "10.1.0.0/16", true)]
    [InlineData("10.1.2.3", "10.2.0.0/16", false)]
    [InlineData("10.1.2.3", "10.1.2.3", true)]
    [InlineData("10.1.2.3", "10.1.2.4", false)]
    [InlineData("10.1.2.3", "0.0.0.0/0", true)]
    [InlineData("::1", "::1/128", true)]
    [InlineData("::1", "10.0.0.0/8", false)]
    [InlineData("10.1.2.3", "::/0", false)]
    [InlineData("203.0.113.9", "*", true)]
    public void Source_Guard_Matches_Cidr(string address, string rule, bool allowed) =>
        Assert.Equal(allowed, McpSourceGuard.IsAllowed(IPAddress.Parse(address), [rule]));

    [Fact]
    public void Source_Guard_Maps_Ipv4_In_Ipv6_Addresses()
    {
        // Kestrel 双栈监听时 IPv4 客户端会以 ::ffff:a.b.c.d 出现，规则写 v4 也要能匹配。
        var mapped = IPAddress.Parse("10.1.2.3").MapToIPv6();
        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.True(McpSourceGuard.IsAllowed(mapped, ["10.0.0.0/8"]));
    }

    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("*", true)]
    [InlineData("::/0", true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("::/129", false)]
    [InlineData("10.0.0.0/x", false)]
    [InlineData("not-an-ip", false)]
    [InlineData("", false)]
    public void Source_Guard_Validates_Rules(string rule, bool valid) =>
        Assert.Equal(valid, McpSourceGuard.IsValidRule(rule));

    [Fact]
    public async Task Write_Tools_Are_Absent_When_Disabled()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            var list = await McpApiTests.RpcAsync(client, "tools/list");
            var names = list.GetProperty("tools").EnumerateArray()
                .Select(x => x.GetProperty("name").GetString()).ToHashSet();

            Assert.Contains("query_history", names);
            Assert.Contains("get_realtime", names);
            Assert.Contains("query_health", names);
            Assert.DoesNotContain("write_tag_value", names);
            Assert.DoesNotContain("start_control_sequence", names);
            Assert.DoesNotContain("publish_screen", names);
            Assert.DoesNotContain("create_device", names);
            Assert.Empty(McpApiTests.WriteTools.Intersect(names));
        }
    }
}
