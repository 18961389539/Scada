using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Engine.Alarms;

namespace Scada.Engine.Tests;

public sealed class AnnunciatorLogicTests
{
    private static AnnunciatorPanel Panel(int? area = null, AnnunciatorLatchMode latch = AnnunciatorLatchMode.Follow) =>
        new() { Id = 1, Name = "P", AreaId = area, Enabled = true, LatchMode = latch, BrowserSound = true, SilenceMinutes = 5 };

    private static AnnunciatorAlarmSample A(AlarmLevel level, int? area = 1, bool active = true, bool unacked = true, bool shelved = false, bool suppressed = false, bool chattering = false) =>
        new(level, area, active, unacked, shelved, suppressed, chattering);

    [Fact]
    public void HighestUnackedPriorityDrivesLights()
    {
        var maps = AnnunciatorLogic.DefaultMaps(1);
        var desired = AnnunciatorLogic.Compute([A(AlarmLevel.Info), A(AlarmLevel.Critical), A(AlarmLevel.Warning)], Panel(), maps, 1000, null);
        Assert.Equal(AlarmLevel.Critical, desired.DrivingLevel);
        Assert.True(desired.Horn);
        Assert.True(desired.TowerRed);
        Assert.False(desired.TowerYellow);
        Assert.True(desired.Flashing);
        Assert.Equal("critical", desired.SoundKey);
    }

    [Fact]
    public void SilenceSuppressesHornButKeepsLights()
    {
        var maps = AnnunciatorLogic.DefaultMaps(1);
        var desired = AnnunciatorLogic.Compute([A(AlarmLevel.Warning)], Panel(), maps, 1000, silenceUntilMs: 5000);
        Assert.True(desired.Silenced);
        Assert.False(desired.Horn);
        Assert.True(desired.Beacon);
        Assert.True(desired.TowerYellow);
        Assert.Null(desired.SoundKey);
    }

    [Fact]
    public void AckedOrClearedOrShelvedDoNotDrive()
    {
        var maps = AnnunciatorLogic.DefaultMaps(1);
        var desired = AnnunciatorLogic.Compute(
        [
            A(AlarmLevel.Critical, unacked: false),
            A(AlarmLevel.Warning, active: false),
            A(AlarmLevel.Warning, shelved: true),
            A(AlarmLevel.Warning, suppressed: true),
            A(AlarmLevel.Critical, chattering: true)
        ], Panel(), maps, 1000, null);
        Assert.Null(desired.DrivingLevel);
        Assert.False(desired.Horn);
    }

    [Fact]
    public void AreaIsolationIgnoresOtherAreas()
    {
        var maps = AnnunciatorLogic.DefaultMaps(1);
        var desired = AnnunciatorLogic.Compute([A(AlarmLevel.Critical, area: 2)], Panel(area: 1), maps, 1000, null);
        Assert.Null(desired.DrivingLevel);
        var global = AnnunciatorLogic.Compute([A(AlarmLevel.Critical, area: 2)], Panel(area: null), maps, 1000, null);
        Assert.Equal(AlarmLevel.Critical, global.DrivingLevel);
    }

    [Fact]
    public void LatchKeepsLightsUntilCleared()
    {
        var maps = AnnunciatorLogic.DefaultMaps(1);
        var panel = Panel(latch: AnnunciatorLatchMode.Latch);
        var active = AnnunciatorLogic.Compute([A(AlarmLevel.Warning)], panel, maps, 1000, null);
        Assert.True(active.TowerYellow);
        var afterClear = AnnunciatorLogic.Compute([], panel, maps, 2000, null);
        var held = AnnunciatorLogic.ApplyLatch(afterClear, active, AnnunciatorLatchMode.Latch, clearLatch: false);
        Assert.True(held.TowerYellow);
        Assert.False(held.Horn);
        var cleared = AnnunciatorLogic.ApplyLatch(afterClear, active, AnnunciatorLatchMode.Latch, clearLatch: true);
        Assert.False(cleared.TowerYellow);
    }

    [Fact]
    public void FlashPhaseTogglesNonHornChannels()
    {
        var d = new AnnunciatorDesired(AlarmLevel.Critical, true, true, true, false, false, false, true, false, "critical");
        Assert.Equal(1, AnnunciatorLogic.ChannelValue(d, AnnunciatorChannelKind.Horn, flashPhaseOn: false));
        Assert.Equal(0, AnnunciatorLogic.ChannelValue(d, AnnunciatorChannelKind.TowerRed, flashPhaseOn: false));
        Assert.Equal(1, AnnunciatorLogic.ChannelValue(d, AnnunciatorChannelKind.TowerRed, flashPhaseOn: true));
        Assert.Equal(0, AnnunciatorLogic.ChannelValue(d with { TowerRed = false }, AnnunciatorChannelKind.TowerRed, true));
    }

    [Fact]
    public void ExpiredSilenceDoesNotSuppressHorn()
    {
        var maps = AnnunciatorLogic.DefaultMaps(1);
        var desired = AnnunciatorLogic.Compute([A(AlarmLevel.Warning)], Panel(), maps, nowMs: 6000, silenceUntilMs: 5000);
        Assert.False(desired.Silenced);
        Assert.True(desired.Horn);
    }
}
