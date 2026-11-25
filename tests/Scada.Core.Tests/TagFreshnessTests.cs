using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Core.Tests;

public sealed class TagFreshnessTests
{
    [Fact]
    public void StaleWindowMatchesAlarmEngineFormula()
    {
        var device = new Device { PollIntervalMs = 1000 };
        var tag = new Tag { Id = 1, DeviceId = 1, ScanRateMs = 2000 };
        Assert.Equal(Math.Max(15_000, 2000 * 3L), TagFreshness.StaleAfterMs(tag, device));

        var fresh = new TagValue(1, 1, TagQuality.Good, 10_000);
        Assert.False(TagFreshness.IsStale(fresh, 10_000 + 15_000, tag, device));
        Assert.True(TagFreshness.IsStale(fresh, 10_000 + 15_001, tag, device));
        Assert.Equal(TagQuality.Bad, TagFreshness.Apply(fresh, 10_000 + 15_001, tag, device).Quality);
    }
}