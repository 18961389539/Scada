using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Core.Protocols;

namespace Scada.Collector.Tests;

// 出站目标策略：默认只连现场内网，公网 IP 必须显式放行；白名单一旦配置，主机名也进入严格模式。
public sealed class OutboundTargetPolicyTests
{
    private static ScadaSettings Allow(params string[] entries) => new() { CollectorTargetAllowlist = [.. entries] };

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public void PrivateTargetsPass(string host) => Assert.Null(OutboundTargetPolicy.Validate(host, new ScadaSettings()));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("203.0.113.5")]
    [InlineData("2001:4860:4860::8888")]
    public void PublicTargetsAreRejected(string host)
    {
        var error = OutboundTargetPolicy.Validate(host, new ScadaSettings());
        Assert.NotNull(error);
        Assert.Contains("公网", error);
    }

    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.5", true)]
    [InlineData("203.0.113.0/24", "203.0.114.5", false)]
    [InlineData("198.51.100.128/25", "198.51.100.200", true)]
    [InlineData("198.51.100.128/25", "198.51.100.100", false)]
    // 私网段的白名单只是冗余配置，未命中也会因"内网地址"直接放行。
    [InlineData("10.20.0.0/16", "10.21.9.7", true)]
    public void CidrRulesDecidePublicTargets(string rule, string host, bool allowed)
    {
        var error = OutboundTargetPolicy.Validate(host, Allow(rule));
        if (allowed) Assert.Null(error); else Assert.NotNull(error);
    }

    [Fact]
    public void ExactIpRulePermitsPublicTarget()
        => Assert.Null(OutboundTargetPolicy.Validate("203.0.113.5", Allow("203.0.113.5")));

    [Fact]
    public void ExplicitOptInAllowsPublicTargets()
        => Assert.Null(OutboundTargetPolicy.Validate("8.8.8.8", new ScadaSettings { AllowPublicCollectorTargets = true }));

    [Fact]
    public void HostNamePassesWithoutAllowlist()
        => Assert.Null(OutboundTargetPolicy.Validate("plc-line1", new ScadaSettings()));

    [Fact]
    public void HostNameMustMatchWhenAllowlistConfigured()
    {
        var error = OutboundTargetPolicy.Validate("plc-line1", Allow("10.0.0.0/8"));
        Assert.NotNull(error);
        Assert.Contains("白名单", error);
    }

    [Fact]
    public void WildcardDomainMatchesSubdomainsOnly()
    {
        var settings = Allow("*.factory.local");
        Assert.Null(OutboundTargetPolicy.Validate("plc1.factory.local", settings));
        Assert.NotNull(OutboundTargetPolicy.Validate("factory.local", settings));
    }

    [Fact]
    public void OpcUaEndpointUsesUriHost()
    {
        var device = new Device { Protocol = ProtocolType.OpcUa, OpcEndpointUrl = "opc.tcp://8.8.8.8:4840" };
        Assert.Contains("公网", OutboundTargetPolicy.ValidateDevice(device, new ScadaSettings()));
        device.OpcEndpointUrl = "opc.tcp://192.168.1.20:4840";
        Assert.Null(OutboundTargetPolicy.ValidateDevice(device, new ScadaSettings()));
    }

    [Fact]
    public void ModbusTcpUsesHostAndPort()
    {
        var device = new Device { Protocol = ProtocolType.ModbusTcp, Host = "203.0.113.9", Port = 502 };
        Assert.NotNull(OutboundTargetPolicy.ValidateDevice(device, new ScadaSettings()));
        device.Host = "10.0.0.9";
        Assert.Null(OutboundTargetPolicy.ValidateDevice(device, new ScadaSettings()));
    }

    [Fact]
    public void SerialDeviceIsNotSubjectToHostPolicy()
    {
        var device = new Device { Protocol = ProtocolType.ModbusRtu, Host = "8.8.8.8", SerialPort = "COM3" };
        Assert.Null(OutboundTargetPolicy.ValidateDevice(device, new ScadaSettings()));
    }

    [Fact]
    public void SimulationDeviceIsNotSubjectToHostPolicy()
    {
        var device = new Device { Protocol = ProtocolType.ModbusTcp, Host = "8.8.8.8", SimulationEnabled = true };
        Assert.Null(OutboundTargetPolicy.ValidateDevice(device, new ScadaSettings()));
    }
}
