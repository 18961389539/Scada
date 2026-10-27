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

    [Fact]
    public void HistoryCapacityIncludesJournalsSpoolAndUsesItsOwnDiskEvenWhenFull()
    {
        using var dir = new TempDir();
        var spool = new HistorySpool(dir.Path);
        File.WriteAllBytes(System.IO.Path.Combine(dir.Path, "raw_202601.db"), new byte[100]);
        File.WriteAllBytes(System.IO.Path.Combine(dir.Path, "agg_2026.db"), new byte[200]);
        File.WriteAllBytes(System.IO.Path.Combine(dir.Path, "raw_202601.db-wal"), new byte[30]);
        File.WriteAllBytes(System.IO.Path.Combine(dir.Path, "raw_202601.db-shm"), new byte[40]);
        Directory.CreateDirectory(spool.DirectoryPath);
        File.WriteAllBytes(System.IO.Path.Combine(dir.Path, "_spool", "pending.ndjson"), new byte[50]);
        File.WriteAllBytes(System.IO.Path.Combine(dir.Path, "extra.dat"), new byte[60]);
        var settings = Settings();
        settings.HistoryRoot = dir.Path;
        settings.DiskFreeWarnGb = 5;
        settings.HistorySizeWarnGb = 0.0000001;
        var watch = new SystemWatch(Options.Create(settings), new RealtimeBus(), spool, NullLogger<SystemWatch>.Instance,
            diskProbe: path => new DiskCapacity(path == dir.Path ? "history-volume" : "config-volume", 100L << 30, path == dir.Path ? 0 : 10L << 30));
        var snapshot = watch.Evaluate();
        var capacity = Assert.IsType<HistoryCapacity>(snapshot.History);
        Assert.True(capacity.ScanComplete);
        Assert.Equal(480, capacity.TotalBytes);
        Assert.Equal(100, capacity.RawBytes);
        Assert.Equal(200, capacity.AggregateBytes);
        Assert.Equal(70, capacity.JournalBytes);
        Assert.Equal(50, capacity.SpoolBytes);
        Assert.Equal(60, capacity.OtherBytes);
        Assert.Equal("history-volume", capacity.Disk.Root);
        Assert.Equal(0, capacity.Disk.FreeBytes);
        Assert.Contains(snapshot.Warnings, warning => warning.Code == "history_disk_low");
        Assert.Contains(snapshot.Warnings, warning => warning.Code == "history_size_high");
        Assert.DoesNotContain(snapshot.Warnings, warning => warning.Code == "disk_low");
    }

    [Fact]
    public void CapacityForecastRequiresWarmupAndNeverReportsNegativeGrowth()
    {
        using var dir = new TempDir();
        var settings = Settings();
        settings.HistoryRoot = dir.Path;
        settings.DiskFreeWarnGb = 0;
        var clock = new Clock();
        var path = System.IO.Path.Combine(dir.Path, "raw_202601.db");
        File.WriteAllBytes(path, new byte[100]);
        var watch = new SystemWatch(Options.Create(settings), new RealtimeBus(), new HistorySpool(dir.Path), NullLogger<SystemWatch>.Instance,
            clock, _ => new DiskCapacity("history", 10000, 1000));
        Assert.Null(watch.Evaluate().History!.GrowthBytesPerDay);
        clock.Now = clock.Now.AddMinutes(10);
        File.WriteAllBytes(path, new byte[200]);
        var grown = watch.Evaluate();
        Assert.Equal(14400d, grown.History!.GrowthBytesPerDay);
        Assert.Equal(1000d / 14400, grown.History.DaysUntilDiskWarn);
        Assert.Contains(grown.Warnings, warning => warning.Code == "history_capacity_forecast");
        clock.Now = clock.Now.AddMinutes(10);
        File.WriteAllBytes(path, new byte[50]);
        var shrunk = watch.Evaluate().History!;
        Assert.Equal(0d, shrunk.GrowthBytesPerDay);
        Assert.Null(shrunk.DaysUntilDiskWarn);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
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
