using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public sealed class SystemWatchTests
{
    [Fact]
    public void HealthySystemProducesNoWarnings()
    {
        using var dir = new TempDir();
        var watch = new SystemWatch(Options.Create(Settings()), new RealtimeBus(), new HistorySpool(dir.Path), NullLogger<SystemWatch>.Instance);

        var snapshot = watch.Evaluate();

        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public async Task SpoolBacklogAboveThresholdIsReported()
    {
        using var dir = new TempDir();
        var spool = new HistorySpool(dir.Path);
        await spool.AppendAsync(new[] { new HistoryValue(1, 1000, 10, TagQuality.Good) }, default);
        var watch = new SystemWatch(Options.Create(Settings(spoolWarnBytes: 1)), new RealtimeBus(), spool, NullLogger<SystemWatch>.Instance);

        var snapshot = watch.Evaluate();

        Assert.Contains(snapshot.Warnings, x => x.Code == "history_spool_backlog");
        Assert.True(snapshot.HistorySpoolBytes > 0);
    }

    [Fact]
    public void BusBacklogAboveThresholdIsReported()
    {
        var bus = new RealtimeBus(); bus.Subscribe();
        bus.Publish(new TagValue(1, 1, TagQuality.Good, ScadaTime.NowMs(), 0));
        var watch = new SystemWatch(Options.Create(Settings(backlogWarn: 0)), bus, new HistorySpool(System.IO.Path.GetTempPath()), NullLogger<SystemWatch>.Instance);

        Assert.Contains(watch.Evaluate().Warnings, x => x.Code == "bus_backlog");
    }

    [Fact]
    public void BusDropsAreReportedOnceAndNotPersistedForever()
    {
        var bus = new RealtimeBus(); bus.Subscribe();
        for (var i = 0; i < 20_001; i++) bus.Publish(new TagValue(1, i, TagQuality.Good, ScadaTime.NowMs(), 0));
        var watch = new SystemWatch(Options.Create(Settings(backlogWarn: 1_000_000)), bus, new HistorySpool(System.IO.Path.GetTempPath()), NullLogger<SystemWatch>.Instance);

        Assert.Contains(watch.Evaluate().Warnings, x => x.Code == "bus_dropped");
        Assert.DoesNotContain(watch.Evaluate().Warnings, x => x.Code == "bus_dropped");
    }

    private static ScadaSettings Settings(long spoolWarnBytes = 8L * 1024 * 1024, int backlogWarn = 10_000) => new()
    {
        DiskFreeWarnGb = 0,
        HistorySpoolWarnBytes = spoolWarnBytes,
        BusBacklogWarn = backlogWarn
    };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scada-watch-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { } }
    }
}
