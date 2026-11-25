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
}
