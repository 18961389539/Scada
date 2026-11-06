using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Server.Tests;

// 采集指纹决定"保存一次配置要不要断开现场设备"。这里锁住两个方向：
// 改连接参数/点位地址必须变（否则改了配置不生效），改报警规则/计算测点必须不变（否则无关保存也会断线）。
public sealed class CollectionFingerprintTests
{
    private static Device Plc() => new() { Id = 1, Name = "PLC-1", Host = "10.0.0.1", Port = 502, Enabled = true };

    private static Tag[] Physical() =>
    [
        new() { Id = 1, DeviceId = 1, Name = "温度", Kind = TagKind.Physical, Address = 0, DataType = TagDataType.Float }
    ];

    private static AlarmRule Threshold() => new() { Id = 1, Key = "high", TagId = 1, Type = AlarmRuleType.High, Threshold = 80 };

    [Fact]
    public void SameConfigurationProducesTheSameFingerprint()
    {
        Assert.Equal(CollectionFingerprint.Of([Plc()], Physical()), CollectionFingerprint.Of([Plc()], Physical()));
    }

    [Fact]
    public void ConnectionParametersChangeTheFingerprint()
    {
        var baseline = CollectionFingerprint.Of([Plc()], Physical());

        var otherPort = Plc(); otherPort.Port = 503;
        var otherHost = Plc(); otherHost.Host = "10.0.0.2";
        var disabled = Plc(); disabled.Enabled = false;

        Assert.NotEqual(baseline, CollectionFingerprint.Of([otherPort], Physical()));
        Assert.NotEqual(baseline, CollectionFingerprint.Of([otherHost], Physical()));
        Assert.NotEqual(baseline, CollectionFingerprint.Of([disabled], Physical()));
    }

    [Fact]
    public void PointAddressAndScalingChangeTheFingerprint()
    {
        var baseline = CollectionFingerprint.Of([Plc()], Physical());

        var moved = Physical(); moved[0].Address = 4;
        var scaled = Physical(); scaled[0].Scale = 0.1;
        var renamed = Physical(); renamed[0].Name = "温度2";

        Assert.NotEqual(baseline, CollectionFingerprint.Of([Plc()], moved));
        Assert.NotEqual(baseline, CollectionFingerprint.Of([Plc()], scaled));
        Assert.NotEqual(baseline, CollectionFingerprint.Of([Plc()], renamed));
    }

    [Fact]
    public void CalculatedTagsDoNotAffectTheAcquisitionFingerprint()
    {
        var baseline = CollectionFingerprint.Of([Plc()], Physical());
        Tag[] withCalculated =
        [
            .. Physical(),
            new() { Id = 2, Name = "平均温度", Kind = TagKind.Calculated, Expression = "(1+2)/2" }
        ];

        Assert.Equal(baseline, CollectionFingerprint.Of([Plc()], withCalculated));
    }

    [Fact]
    public void AlarmRuleEditsDoNotTriggerReconnectsButDoChangeTheFullFingerprint()
    {
        var devices = new[] { Plc() };
        var tags = Physical();
        var area = new Area { Id = Area.DefaultId, Name = "默认区域" };
        var rule = Threshold();

        var acquisition = CollectionFingerprint.Of(devices, tags);
        var full = CollectionFingerprint.OfAll(devices, tags, [area], [rule]);

        rule.Threshold = 90;
        rule.Level = AlarmLevel.Critical;

        Assert.Equal(acquisition, CollectionFingerprint.Of(devices, tags));
        Assert.NotEqual(full, CollectionFingerprint.OfAll(devices, tags, [area], [rule]));
    }

    [Fact]
    public void EntityOrderDoesNotMatter()
    {
        var second = new Device { Id = 2, Name = "PLC-2", Host = "10.0.0.3", Port = 502, Enabled = true };
        var secondTag = new Tag { Id = 2, DeviceId = 2, Name = "压力", Kind = TagKind.Physical, Address = 0 };

        var forward = CollectionFingerprint.Of([Plc(), second], [.. Physical(), secondTag]);
        var reversed = CollectionFingerprint.Of([second, Plc()], [secondTag, .. Physical()]);

        Assert.Equal(forward, reversed);
    }
}
