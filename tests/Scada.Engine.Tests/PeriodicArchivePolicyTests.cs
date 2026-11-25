using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public sealed class PeriodicArchivePolicyTests
{
    [Fact]
    public void DoesNotRewriteTimestampOrArchiveStaleOrDuplicateSamples()
    {
        var tag = new Tag { Id = 7, ArchiveMode = ArchiveMode.Periodic, ArchivePeriodSec = 1, Deadband = 0 };
        var current = new TagValue(7, 12.5, TagQuality.Good, Ts: 1_000);
        Assert.True(ArchivePolicy.TryStorePeriodic(tag, current, lastStored: null, nowMs: 1_200, device: null, out var first));
        Assert.Equal(1_000, first.Ts);
        Assert.Equal(12.5, first.Value);

        var last = new ArchiveState(first.Ts, first.Value, first.Quality);
        Assert.False(ArchivePolicy.TryStorePeriodic(tag, current, last, nowMs: 3_000, device: null, out _));

        var stale = current with { Ts = 1_000 };
        Assert.False(ArchivePolicy.TryStorePeriodic(tag, stale, lastStored: null, nowMs: 1_000 + 15_001, device: null, out _));
    }
}