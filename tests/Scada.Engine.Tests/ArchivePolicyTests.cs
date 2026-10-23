using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public class ArchivePolicyTests
{
    private static Tag Tag(ArchiveMode mode, double deadband = 0.5, int periodSec = 600, TagDataType type = TagDataType.Float) =>
        new() { Id = 1, ArchiveMode = mode, Deadband = deadband, ArchivePeriodSec = periodSec, DataType = type };

    [Fact]
    public void FirstValue_IsAlwaysStored()
    {
        var tag = Tag(ArchiveMode.OnChange);
        Assert.True(ArchivePolicy.ShouldStore(tag, new TagValue(1, 10, TagQuality.Good, 1_000), null, 1_000));
    }

    [Fact]
    public void None_IsNeverStored()
    {
        var tag = Tag(ArchiveMode.None);
        Assert.False(ArchivePolicy.ShouldStore(tag, new TagValue(1, 10, TagQuality.Good, 1_000), null, 1_000));
    }

    [Fact]
    public void OnChange_UsesDeadband()
    {
        var tag = Tag(ArchiveMode.OnChange, deadband: 1);
        var last = new ArchiveState(1_000, 10, TagQuality.Good);

        Assert.False(ArchivePolicy.ShouldStore(tag, new TagValue(1, 10.5, TagQuality.Good, 2_000), last, 2_000));
        Assert.False(ArchivePolicy.ShouldStore(tag, new TagValue(1, 11, TagQuality.Good, 2_000), last, 2_000));
        Assert.True(ArchivePolicy.ShouldStore(tag, new TagValue(1, 11.1, TagQuality.Good, 2_000), last, 2_000));
    }

    [Fact]
    public void Periodic_ForcesStoreAfterPeriod()
    {
        var tag = Tag(ArchiveMode.Periodic, deadband: 1, periodSec: 60);
        var last = new ArchiveState(1_000, 10, TagQuality.Good);

        Assert.False(ArchivePolicy.ShouldStore(tag, new TagValue(1, 10.2, TagQuality.Good, 30_000), last, 30_000));
        Assert.True(ArchivePolicy.ShouldStore(tag, new TagValue(1, 10.2, TagQuality.Good, 61_000), last, 61_000));
    }

    [Fact]
    public void Bool_OnlyStoresOnToggle()
    {
        var tag = Tag(ArchiveMode.OnChange, type: TagDataType.Bool);
        var last = new ArchiveState(1_000, 1, TagQuality.Good);

        Assert.False(ArchivePolicy.ShouldStore(tag, new TagValue(1, 1, TagQuality.Good, 2_000), last, 2_000));
        Assert.True(ArchivePolicy.ShouldStore(tag, new TagValue(1, 0, TagQuality.Good, 3_000), last, 3_000));
    }

    [Fact]
    public void QualityChange_IsStoredImmediately()
    {
        var tag = Tag(ArchiveMode.OnChange);
        var last = new ArchiveState(1_000, 10, TagQuality.Good);

        Assert.True(ArchivePolicy.ShouldStore(tag, new TagValue(1, 10, TagQuality.CommFailure, 2_000), last, 2_000));
    }
}
