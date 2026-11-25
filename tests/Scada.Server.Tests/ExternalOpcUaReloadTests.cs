using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Server.External.OpcUa;

namespace Scada.Server.Tests;

public sealed class ExternalOpcUaReloadTests
{
    [Fact]
    public void AllowWriteDefaultsToFalse()
    {
        Assert.False(new ExternalOpcUaOptions().AllowWrite);
    }

    [Fact]
    public void AlarmRuleEditsDoNotRestartButTagOrWritableChangesDo()
    {
        var devices = new[] { new Device { Id = 1, Name = "d", AreaId = 1 } };
        var tags = new[] { new Tag { Id = 1, Name = "t", DeviceId = 1, AreaId = 1, Writable = false } };
        var areas = new[] { new Area { Id = 1, Name = "A" } };
        var space = CollectionFingerprint.OfAddressSpace(devices, tags, areas);

        var rule = new AlarmRule { Id = 1, TagId = 1, Key = "high", Type = AlarmRuleType.High, Threshold = 10 };
        var fullBefore = CollectionFingerprint.OfAll(devices, tags, areas, [rule]);
        rule.Threshold = 99;
        Assert.Equal(space, CollectionFingerprint.OfAddressSpace(devices, tags, areas));
        Assert.NotEqual(fullBefore, CollectionFingerprint.OfAll(devices, tags, areas, [rule]));

        var opt = new ExternalOpcUaOptions { EndpointUrl = "opc.tcp://0.0.0.0:4840/Scada", SecurityModes = "None" };
        var host = ExternalOpcUaReload.HostFingerprint(opt);
        Assert.Null(ExternalOpcUaReload.DescribeRestart(host, host, space, space));

        tags[0].Writable = true;
        var afterWritable = CollectionFingerprint.OfAddressSpace(devices, tags, areas);
        Assert.Contains("重建地址空间", ExternalOpcUaReload.DescribeRestart(host, host, space, afterWritable));

        var added = new[] { tags[0], new Tag { Id = 2, Name = "n", DeviceId = 1, AreaId = 1 } };
        Assert.Contains("重建地址空间", ExternalOpcUaReload.DescribeRestart(host, host, space, CollectionFingerprint.OfAddressSpace(devices, added, areas)));

        var signed = new ExternalOpcUaOptions { EndpointUrl = opt.EndpointUrl, SecurityModes = "Sign" };
        Assert.Contains("整站重启", ExternalOpcUaReload.DescribeRestart(host, ExternalOpcUaReload.HostFingerprint(signed), space, space));
    }
}
