using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.Control;

namespace Scada.Engine.Tests;

public class ControlQualityCheckTests
{
    [Fact]
    public void DescribeQuality_ExplainsCommFailureAndBad()
    {
        var missing = ControlCommandService.DescribeQuality(null);
        Assert.Null(missing.Quality);
        Assert.Equal("尚无采样值", missing.Reason);
        Assert.Null(ControlCommandService.QualityRefusal(null));

        var good = new TagValue(1, 12, TagQuality.Good, 1);
        Assert.Equal(TagQuality.Good, ControlCommandService.DescribeQuality(good).Quality);
        Assert.Null(ControlCommandService.QualityRefusal(good));

        var comm = new TagValue(1, 12, TagQuality.CommFailure, 1, Error: "本机已停采");
        Assert.Equal("通讯中断（本机已停采）", ControlCommandService.DescribeQuality(comm).Reason);
        Assert.Equal("测点通讯中断（本机已停采），不能下发", ControlCommandService.QualityRefusal(comm));

        var bad = new TagValue(1, 12, TagQuality.Bad, 1);
        Assert.Equal("质量无效", ControlCommandService.DescribeQuality(bad).Reason);
        Assert.Equal("测点质量无效，不能下发", ControlCommandService.QualityRefusal(bad));
    }

    [Fact]
    public void SimulatedGood_IsNotOfficialCurrent_UnlessTargetDeviceIsSimulated()
    {
        var live = new Device { Name = "泵", SimulationEnabled = false };
        var drill = new Device { Name = "泵", SimulationEnabled = true };
        var simulated = new TagValue(1, 12, TagQuality.Good, 1, Simulated: true);
        var official = new TagValue(1, 12, TagQuality.Good, 1);

        Assert.Null(ControlCommandService.UsableCurrentValue(simulated, live));
        Assert.Equal(12, ControlCommandService.UsableCurrentValue(simulated, drill));
        Assert.Equal(12, ControlCommandService.UsableCurrentValue(official, live));
        Assert.Equal("当前值为仿真，不能作为正式当前值", ControlCommandService.DescribeQuality(simulated, live).Reason);
        Assert.Null(ControlCommandService.DescribeQuality(simulated, live).Quality);
        Assert.Equal(TagQuality.Good, ControlCommandService.DescribeQuality(simulated, drill).Quality);
        Assert.Null(ControlCommandService.QualityRefusal(simulated));
    }
}
