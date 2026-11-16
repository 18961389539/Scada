using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Engine.Realtime;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
namespace Scada.Engine.Tests;

public sealed class HistorySizingPlanTests
{
    [Fact]
    public void UsesCadenceOverrideAndDoesNotInventDeadbandSavings()
    {
        Device[] devices = [new() { Id = 1, PollIntervalMs = 1000 }];
        Tag[] tags = [new() { DeviceId = 1, ScanRateMs = 60000, Deadband = 9999 }];
        var plan = HistorySizingPlan.Estimate(devices, tags, new());
        Assert.Equal(1d / 60, plan.SamplesPerSecond, 8);
        Assert.Equal(100d / 60 * 86400 * 30.44 * 7, plan.RawScenarioBytes, 2);
        tags[0].Deadband = 0;
        Assert.Equal(plan, HistorySizingPlan.Estimate(devices, tags, new()));
    }

    [Fact]
    public void ExcludesDisabledNoneAndUnmodelledJobs()
    {
        Device[] devices = [new() { Id = 1, Enabled = false }];
        Tag[] tags = [new() { DeviceId = 1 }, new() { Kind = TagKind.Calculated, CalculationEnabled = false }, new() { Kind = TagKind.Derived }, new() { Kind = TagKind.Calculated, ArchiveMode = ArchiveMode.None }];
        var plan = HistorySizingPlan.Estimate(devices, tags, new());
        Assert.Equal(0, plan.ArchivedTags);
        Assert.Equal(1, plan.UnmodelledDerivedTags);
    }

    [Fact]
    public void PeriodicIsNotAnArchiveRateCap()
    {
        Tag[] tags = [new() { Kind = TagKind.Calculated, ArchiveMode = ArchiveMode.Periodic, ArchivePeriodSec = 600 }];
        Assert.True(HistorySizingPlan.Estimate([], tags, new()).SamplesPerSecond >= 1);
    }

    [Fact]
    public void LargeSiteWithNoSizeGuardIsWarnedBeforeDiskFills()
    {
        var tags = Enumerable.Range(1, 5000).Select(id => new Tag { Id = id, DeviceId = 1 }).ToArray();
        var settings = new ScadaSettings();
        var plan = HistorySizingPlan.Estimate([new() { Id = 1 }], tags, settings);
        var capacity = new HistoryCapacity(DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, true,
            new DiskCapacity("test", 512L << 30, 400L << 30), null, null, 6, 365, 0);
        var warnings = plan.Warnings(settings, capacity).ToList();
        Assert.Contains(warnings, x => x.Code == "history_size_guard_disabled");
        Assert.Contains(warnings, x => x.Code == "history_config_capacity_risk" && x.Message.Contains("NOT a forecast"));
        settings.HistorySizeWarnGb = 250;
        Assert.DoesNotContain(plan.Warnings(settings, capacity), x => x.Code == "history_size_guard_disabled");
    }
    [Fact]
    public void SystemWatchSurfacesConfigurationRiskAndRechecksConfiguration()
    {
        using var temp = new TempDir();
        var devices = new[] { new Device { Id = 1 } };
        var tags = Enumerable.Range(1, 5000).Select(id => new Tag { Id = id, DeviceId = 1 }).ToArray();
        var snapshot = new Snapshot(devices, tags);
        var settings = new ScadaSettings { ConfigDbPath = Path.Combine(temp.Path, "config.db"), HistoryRoot = temp.Path };
        var watch = new SystemWatch(Options.Create(settings), new RealtimeBus(), new HistorySpool(temp.Path), NullLogger<SystemWatch>.Instance,
            diskProbe: _ => new DiskCapacity("test", 512L << 30, 400L << 30), config: snapshot);
        Assert.Contains(watch.Evaluate().Warnings, x => x.Code == "history_config_capacity_risk");
        devices[0].Enabled = false;
        Assert.DoesNotContain(watch.Evaluate().Warnings, x => x.Code.StartsWith("history_config_"));
    }

    private sealed class Snapshot(IReadOnlyList<Device> devices, IReadOnlyList<Tag> tags) : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => devices;
        public IReadOnlyList<Tag> Tags => tags;
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag tag) { tag = null!; return false; }
        public bool TryGetDevice(int id, out Device device) { device = null!; return false; }
    }

}
