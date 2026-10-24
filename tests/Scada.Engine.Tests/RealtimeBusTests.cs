using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public sealed class RealtimeBusTests
{
    [Fact]
    public void FullSubscriberRejectsNewValuesAndCountsThemInsteadOfSilentlyEvictingQueuedData()
    {
        var bus = new RealtimeBus(); var reader = bus.Subscribe();
        for (var i = 0; i < 20_000; i++) bus.Publish(new TagValue(1, i, TagQuality.Good, ScadaTime.NowMs(), 0));
        Assert.Equal(0, bus.DroppedTotal);
        Assert.Equal(20_000, bus.Backlog);

        bus.Publish(new TagValue(1, -1, TagQuality.Good, ScadaTime.NowMs(), 0));

        Assert.Equal(1, bus.DroppedTotal);
        Assert.Equal(20_000, bus.Backlog);
        Assert.True(reader.TryRead(out var head));
        Assert.Equal(0, head.Value);
    }

    [Fact]
    public void DroppingIsCountedPerSubscriber()
    {
        var bus = new RealtimeBus(); bus.Subscribe(); bus.Subscribe();
        for (var i = 0; i < 20_001; i++) bus.Publish(new TagValue(1, i, TagQuality.Good, ScadaTime.NowMs(), 0));

        Assert.Equal(2, bus.DroppedTotal);
    }
}
