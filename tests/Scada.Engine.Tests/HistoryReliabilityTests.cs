using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data.Infrastructure;
using Scada.Engine.History;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public sealed class HistoryReliabilityTests
{
    [Fact]
    public async Task RepeatedCalculationScansInOneBatchArchiveOnlyValueAndQualityTransitions()
    {
        using var dir = new TempDir(); using var bus = new ObservedBus(); var store = new FlakyStore();
        var tag = new Tag { Id = 1, Name = "constant calculation", Kind = TagKind.Calculated, DataType = TagDataType.Double };
        using var writer = new HistoryWriterService(bus, new RealtimeCache(), new Snapshot(tag), store,
            Options.Create(new ScadaSettings { ArchiveFlushIntervalMs = 500, ArchiveBatchSize = 2000, HistoryRoot = dir.Path }), new HistorySpool(dir.Path), NullLogger<HistoryWriterService>.Instance);
        await writer.StartAsync(default); await bus.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (var i = 0; i < 100; i++) bus.Publish(new(1, 42, TagQuality.Good, 1000 + i, DefinitionRevision: 1));
            bus.Publish(new(1, 42, TagQuality.Uncertain, 1101, DefinitionRevision: 1));
            bus.Publish(new(1, 42, TagQuality.Good, 1102, DefinitionRevision: 1));
            bus.Publish(new(1, 43, TagQuality.Good, 1103, DefinitionRevision: 1));
            await Until(() => store.Attempts > 0);
        }
        finally { await writer.StopAsync(default); }
        var written = store.Written.ToArray(); Assert.Equal(4, written.Length);
        Assert.Equal(new[] { 1000L, 1101L, 1102L, 1103L }, written.Select(x => x.Ts));
    }

    private static HistoryValue[] Batch(int tagId, long ts) => new[] { new HistoryValue(tagId, ts, tagId * 10, TagQuality.Good) };

    [Fact]
    public async Task SpilledBatchesSurviveRestartAndReplayInOrder()
    {
        using var dir = new TempDir();
        var before = new HistorySpool(dir.Path);
        await before.AppendAsync(Batch(1, 1000), default);
        await before.AppendAsync(Batch(2, 2000), default);
        Assert.True(before.PendingBytes > 0);

        var after = new HistorySpool(dir.Path);
        var written = new List<HistoryValue>();
        await after.DrainAsync(batch => { written.AddRange(batch); return Task.CompletedTask; }, default);

        Assert.Equal(new[] { 1, 2 }, written.Select(x => x.TagId));
        Assert.Equal(0, after.PendingBytes);
        Assert.Equal(0, after.SegmentCount);
        Assert.Empty(Directory.Exists(after.DirectoryPath) ? Directory.GetFiles(after.DirectoryPath, "history-spool*.ndjson") : []);
    }

    [Fact]
    public async Task DrainStopsAtFirstFailureAndKeepsUnwrittenBatches()
    {
        using var dir = new TempDir(); var spool = new HistorySpool(dir.Path);
        for (var i = 1; i <= 3; i++) await spool.AppendAsync(Batch(i, i * 1000), default);

        var attempted = new List<int>();
        await Assert.ThrowsAsync<IOException>(() => spool.DrainAsync(batch =>
        {
            var tagId = batch.First().TagId; attempted.Add(tagId);
            return tagId == 2 ? Task.FromException(new IOException("history db unavailable")) : Task.CompletedTask;
        }, default));

        Assert.Equal(new[] { 1, 2 }, attempted);
        Assert.True(spool.PendingBytes > 0);

        var written = new List<int>();
        await spool.DrainAsync(batch => { written.AddRange(batch.Select(x => x.TagId)); return Task.CompletedTask; }, default);

        Assert.Equal(new[] { 2, 3 }, written);
        Assert.Equal(0, spool.PendingBytes);
    }

    [Fact]
    public async Task SpoolRefusesBatchesBeyondItsLimitAndCountsThem()
    {
        using var dir = new TempDir(); var spool = new HistorySpool(dir.Path, maxBytes: 512);
        var rejected = 0;
        for (var i = 1; i <= 20; i++)
        {
            try { await spool.AppendAsync(Batch(i, i * 1000), default); }
            catch (IOException) { rejected++; break; }
        }

        Assert.Equal(1, rejected);
        Assert.Equal(1, spool.DroppedBatches);
        Assert.True(spool.PendingBytes > 0);
    }

    [Fact]
    public async Task HistoryWriterSpillsWhenStoreFailsAndReplaysAfterRecovery()
    {
        using var dir = new TempDir();
        var bus = new RealtimeBus(); var store = new FlakyStore { Fail = true }; var spool = new HistorySpool(dir.Path);
        var settings = Options.Create(new ScadaSettings { ArchiveFlushIntervalMs = 100, ArchiveBatchSize = 8, HistoryRoot = dir.Path });
        var writer = new HistoryWriterService(bus, new RealtimeCache(), new Snapshot(new Tag { Id = 1, DeviceId = 1, ArchiveMode = ArchiveMode.OnChange }), store, settings, spool, NullLogger<HistoryWriterService>.Instance);
        await writer.StartAsync(default);
        try
        {
            // 采集器在宿主启动后才开始轮询，这里同样持续发布直到写入服务确实订阅并处理。
            for (var i = 0; i < 100 && spool.PendingBytes == 0; i++)
            {
                bus.Publish(new TagValue(1, 10 + i, TagQuality.Good, ScadaTime.NowMs(), 0));
                await Task.Delay(50);
            }
            Assert.True(spool.PendingBytes > 0, "写库失败后批次应转入磁盘暂存");

            store.Fail = false;
            await Until(() => spool.PendingBytes == 0 && store.Written.Count > 0, () => $"pending={spool.PendingBytes} written={store.Written.Count}");

            Assert.NotEmpty(store.Written);
            Assert.All(store.Written, x => Assert.Equal(1, x.TagId));
            Assert.True(store.Written.Select(x => x.Value).SequenceEqual(store.Written.Select(x => x.Value).OrderBy(x => x)));
        }
        finally { await writer.StopAsync(default); writer.Dispose(); }
    }

    [Fact]
    public async Task IdleWriterKeepsRetryingAPersistedBacklogDuringAProlongedOutage()
    {
        using var dir = new TempDir(); var spool = new HistorySpool(dir.Path);
        await spool.AppendAsync(Batch(1, 1000), default);
        var store = new FlakyStore { Fail = true };
        var settings = Options.Create(new ScadaSettings { ArchiveFlushIntervalMs = 200, HistoryRoot = dir.Path });
        using var writer = new HistoryWriterService(new RealtimeBus(), new RealtimeCache(), new Snapshot(), store, settings, spool, NullLogger<HistoryWriterService>.Instance);
        await writer.StartAsync(default);
        try
        {
            await Until(() => store.Attempts >= 3 || writer.ExecuteTask!.IsCompleted);
            Assert.False(writer.ExecuteTask!.IsCompleted, writer.ExecuteTask.Exception?.ToString());
            Assert.True(spool.PendingBytes > 0);
            store.Fail = false;
            await Until(() => spool.PendingBytes == 0 && store.Written.Count == 1);
            Assert.Equal(1000, store.Written.Single().Ts);
        }
        finally { await writer.StopAsync(default); }
    }

    [Theory]
    [InlineData(HistoryResolution.Raw, 100)]
    [InlineData(HistoryResolution.FiveMinute, 60000)]
    [InlineData(HistoryResolution.Hour, 3600000)]
    public async Task TrendQueryStreamsAcrossYearBoundaryWithinTotalPointBudget(HistoryResolution resolution, long step)
    {
        using var dir = new TempDir();
        var store = new HistoryStore(new HistoryDbFactory(Options.Create(new ScadaSettings { HistoryRoot = dir.Path })));
        var start = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var values = Enumerable.Range(0, 500).SelectMany(index => new[] { 1, 2 }.Select(tagId =>
            new HistoryValue(tagId, start + index * step, index == 250 ? 999 : 1, index == 251 ? TagQuality.Bad : TagQuality.Good))).ToArray();
        if (resolution == HistoryResolution.Raw) await store.WriteRawAsync(values, default);
        else
        {
            var aggregates = values.Select(x => new AggregateValue(x.TagId, x.Ts, x.Value, x.Value, x.Value, 1)).ToArray();
            if (resolution == HistoryResolution.FiveMinute) await store.WriteAggregate5MinAsync(aggregates, default);
            else await store.WriteAggregateHourAsync(aggregates, default);
        }
        var result = await new HistoryQueryService(store).QueryAsync([1, 2], start, start + 499 * step, maxPoints: 100);
        Assert.Equal(resolution, result.Resolution);
        Assert.True(result.Downsampled);
        Assert.Equal(2, result.Series.Count);
        Assert.InRange(result.Series.Sum(x => x.Points.Count), 4, 100);
        foreach (var series in result.Series)
        {
            Assert.Equal(start, series.Points[0].Ts);
            Assert.Equal(start + 499 * step, series.Points[^1].Ts);
            Assert.Contains(series.Points, point => point.Value == 999);
            if (resolution == HistoryResolution.Raw) Assert.Contains(series.Points, point => point.Quality == TagQuality.Bad);
        }
    }

    [Fact]
    public async Task QueryBudgetsRejectIncompleteResultsAndReleaseConcurrencySlot()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(new HistoryDbFactory(Options.Create(new ScadaSettings { HistoryRoot = dir.Path })));
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await store.WriteRawAsync([new HistoryValue(1, start, 1, TagQuality.Good), new HistoryValue(1, start + 1, 2, TagQuality.Bad)], default);
        using var query = new HistoryQueryService(store, Options.Create(new ScadaSettings
        {
            HistoryQueryMaxConcurrent = 1, HistoryQueryMaxReadPoints = 1, HistoryAnalysisMaxPoints = 1
        }));
        var trendError = await Assert.ThrowsAsync<HistoryQueryException>(() => query.QueryAsync([1], start, start + 1, maxPoints: 10));
        Assert.Equal("history_query_read_limit", trendError.Code);
        var analysisError = await Assert.ThrowsAsync<HistoryQueryException>(() => query.QueryRawExactAsync([1], start, start + 1));
        Assert.Equal("history_query_read_limit", analysisError.Code);
        Assert.Equal(2, query.ReadLimitedQueries);
        Assert.Equal(0, query.ActiveQueries);
        Assert.Single((await query.QueryRawExactAsync([1], start, start)).Single().Points);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueryDeadlineAndCallerCancellationReleaseBusySlot(bool deadline)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<HistoryValue> Block([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }
        var store = new FlakyStore { ReadValues = Block };
        using var query = new HistoryQueryService(store, Options.Create(new ScadaSettings
        {
            HistoryQueryMaxConcurrent = 1, HistoryQueryTimeoutSeconds = 1
        }));
        using var cancel = new CancellationTokenSource();
        var running = query.QueryAsync([1], 0, 1, cancel.Token, 10);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = await Assert.ThrowsAsync<HistoryQueryException>(() => query.QueryAsync([1], 0, 1, maxPoints: 10));
        Assert.Equal("history_query_busy", busy.Code);
        if (deadline)
        {
            var timeout = await Assert.ThrowsAsync<HistoryQueryException>(() => running);
            Assert.Equal("history_query_timeout", timeout.Code);
            Assert.Equal(1, query.TimedOutQueries);
        }
        else
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(0, query.TimedOutQueries);
        }
        Assert.Equal(0, query.ActiveQueries);
        Assert.Equal(1, query.RejectedQueries);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(30, 1)]
    public async Task RetentionKeepsRecentDataAndOnlyExpiresHoursWhenExplicitlyConfigured(int hourDays, int expectedHours)
    {
        using var dir = new TempDir();
        var settings = new ScadaSettings
        {
            ConfigDbPath = Path.Combine(dir.Path, "missing-config.db"), HistoryRoot = Path.Combine(dir.Path, "history"),
            BackupRoot = Path.Combine(dir.Path, "backups"), HistoryRetentionMonths = 1,
            Agg5MinRetentionDays = 7, AggHourRetentionDays = hourDays, AggregationEnabled = false
        };
        var factory = new HistoryDbFactory(Options.Create(settings));
        var store = new HistoryStore(factory);
        var now = DateTimeOffset.UtcNow;
        var current = now.AddDays(-1).ToUnixTimeMilliseconds();
        var oldFive = now.AddDays(-10).ToUnixTimeMilliseconds();
        var oldHour = now.AddDays(-40).ToUnixTimeMilliseconds();
        await store.WriteAggregate5MinAsync([new AggregateValue(1, oldFive, 1, 1, 1, 1), new AggregateValue(1, current, 2, 2, 2, 1)], default);
        await store.WriteAggregateHourAsync([new AggregateValue(1, oldHour, 1, 1, 1, 1), new AggregateValue(1, current, 2, 2, 2, 1)], default);
        var oldMonth = now.AddMonths(-3);
        var expired = factory.GetRawPath(oldMonth.Year, oldMonth.Month);
        var recent = factory.GetRawPath(now.Year, now.Month);
        await store.WriteRawAsync([new HistoryValue(1, oldMonth.ToUnixTimeMilliseconds(), 1, TagQuality.Good),
            new HistoryValue(1, now.ToUnixTimeMilliseconds(), 2, TagQuality.Good)], default);
        using var maintenance = new Scada.Engine.Maintenance.MaintenanceService(Options.Create(settings), factory,
            NullLogger<Scada.Engine.Maintenance.MaintenanceService>.Instance);
        await maintenance.RunOnceAsync(default);
        Assert.False(File.Exists(expired));
        Assert.False(File.Exists(expired + "-wal"));
        Assert.True(File.Exists(recent));
        await store.WriteRawAsync([new HistoryValue(1, oldMonth.ToUnixTimeMilliseconds(), 3, TagQuality.Good)], default);
        Assert.Equal(3, (await store.QueryRawAsync([1], oldMonth.ToUnixTimeMilliseconds(), oldMonth.ToUnixTimeMilliseconds(), default)).Single().Points.Single().Value);
        Assert.Single((await store.QueryAggregate5MinAsync([1], oldHour, now.ToUnixTimeMilliseconds(), default)).Single().Points);
        Assert.Equal(expectedHours, (await store.QueryAggregateHourAsync([1], oldHour, now.ToUnixTimeMilliseconds(), default)).Single().Points.Count);
    }

    [Fact]
    public async Task RawHistoryReplayAfterCommitIsIdempotentAndStillWritesFollowingValues()
    {
        using var dir = new TempDir();
        var settings = Options.Create(new ScadaSettings { HistoryRoot = dir.Path });
        var store = new HistoryStore(new HistoryDbFactory(settings));
        var ts = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await store.WriteRawAsync(Batch(1, ts), default);
        // The process can stop after a database commit and before removing its spool batch.
        await store.WriteRawAsync([.. Batch(1, ts), .. Batch(2, ts + 1)], default);
        await store.WriteRawAsync([new HistoryValue(1, ts, 999, TagQuality.Bad)], default);
        var series = await store.QueryRawAsync([1, 2], ts, ts + 1, default);
        Assert.Equal(2, series.Sum(x => x.Points.Count));
        Assert.Equal(10, series.Single(x => x.TagId == 1).Points.Single().Value);
        Assert.Equal(TagQuality.Good, series.Single(x => x.TagId == 1).Points.Single().Quality);
    }

    [Fact]
    public async Task SpoolBackupWaitsForReplayAndCopiesOnlyTheRemainingCompleteBatches()
    {
        using var dir = new TempDir(); var spool = new HistorySpool(dir.Path);
        await spool.AppendAsync(Batch(1, 1000), default); await spool.AppendAsync(Batch(2, 2000), default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = spool.DrainAsync(async batch =>
        {
            if (batch.First().TagId == 1) { started.SetResult(); await release.Task; }
            else throw new IOException("second batch unavailable");
        }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var copy = spool.CopyToAsync(Path.Combine(dir.Path, "snapshot.ndjson"), default);
        try { Assert.False(copy.IsCompleted); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<IOException>(() => drain); Assert.True(await copy);
        var lines = await File.ReadAllLinesAsync(Path.Combine(dir.Path, "snapshot.ndjson"));
        var recovered = System.Text.Json.JsonSerializer.Deserialize<HistoryValue[]>(Assert.Single(lines), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(2, Assert.Single(recovered!).TagId);
    }


    [Fact]
    public async Task AppendRollsToNewSegmentWhenSegmentLimitReached()
    {
        using var dir = new TempDir();
        var spool = new HistorySpool(dir.Path, maxBytes: 64 * 1024, segmentBytes: 256);
        for (var i = 1; i <= 20; i++) await spool.AppendAsync(Batch(i, i * 1000), default);
        var files = Directory.GetFiles(spool.DirectoryPath, "history-spool-*.ndjson");
        Assert.True(files.Length >= 2, $"expected rolled segments, got {files.Length}");
        Assert.Equal(files.Length, spool.SegmentCount);

        var written = new List<int>();
        await spool.DrainAsync(batch => { written.AddRange(batch.Select(x => x.TagId)); return Task.CompletedTask; }, default);
        Assert.Equal(Enumerable.Range(1, 20), written);
        Assert.Equal(0, spool.PendingBytes);
        Assert.Equal(0, spool.SegmentCount);
    }

    [Fact]
    public async Task CrossSegmentDrainPreservesAppendOrder()
    {
        using var dir = new TempDir();
        var spool = new HistorySpool(dir.Path, maxBytes: 64 * 1024, segmentBytes: 180);
        for (var i = 1; i <= 8; i++) await spool.AppendAsync(Batch(i, i * 100), default);
        Assert.True(spool.SegmentCount >= 2);

        var written = new List<int>();
        await spool.DrainAsync(batch => { written.AddRange(batch.Select(x => x.TagId)); return Task.CompletedTask; }, default);
        Assert.Equal(Enumerable.Range(1, 8), written);
    }

    [Fact]
    public async Task CorruptLinesAndTruncatedTailDoNotBlockGoodBatches()
    {
        using var dir = new TempDir();
        var spool = new HistorySpool(dir.Path, maxBytes: 64 * 1024, segmentBytes: 4 * 1024);
        await spool.AppendAsync(Batch(1, 1000), default);
        await spool.AppendAsync(Batch(2, 2000), default);
        var path = Directory.GetFiles(spool.DirectoryPath, "history-spool*.ndjson").Single();
        var good = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, good + "{not-json\n" + good.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1] + "\n" + "{\"partial\":");

        var written = new List<int>();
        await spool.DrainAsync(batch => { written.AddRange(batch.Select(x => x.TagId)); return Task.CompletedTask; }, default);

        Assert.Equal(new[] { 1, 2, 2 }, written);
        Assert.True(spool.CorruptLines >= 1);
        Assert.Equal(0, spool.PendingBytes);
        Assert.True(File.Exists(path + ".corrupt") || spool.CorruptLines >= 2);
    }

    [Fact]
    public async Task LegacySingleFileSpoolStillDrains()
    {
        using var dir = new TempDir();
        var spoolDir = Path.Combine(dir.Path, "_spool");
        Directory.CreateDirectory(spoolDir);
        var legacy = Path.Combine(spoolDir, HistorySpool.LegacyFileName);
        var line1 = System.Text.Json.JsonSerializer.Serialize(Batch(1, 1000), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var line2 = System.Text.Json.JsonSerializer.Serialize(Batch(2, 2000), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(legacy, line1 + "\n" + line2 + "\n");

        var spool = new HistorySpool(dir.Path);
        Assert.True(spool.PendingBytes > 0);
        var written = new List<int>();
        await spool.DrainAsync(batch => { written.AddRange(batch.Select(x => x.TagId)); return Task.CompletedTask; }, default);
        Assert.Equal(new[] { 1, 2 }, written);
        Assert.False(File.Exists(legacy));
        Assert.Equal(0, spool.PendingBytes);
    }

    [Fact]
    public async Task DropOldestOnFullCanMakeRoomWhenEnabled()
    {
        using var dir = new TempDir();
        var spool = new HistorySpool(dir.Path, maxBytes: 600, segmentBytes: 200, dropOldestOnFull: true);
        for (var i = 1; i <= 30; i++)
        {
            try { await spool.AppendAsync(Batch(i, i * 1000), default); }
            catch (IOException) { break; }
        }
        Assert.True(spool.SpilledBatches >= 1);
        // 开启丢旧后，应仍能继续写入而不是永久拒收（除非单批大于上限）。
        await spool.AppendAsync(Batch(999, 999000), default);
        Assert.True(spool.DroppedBatches >= 1);
    }

    [Fact]
    public async Task FreeSpaceBetweenMinAndWarnStillSpills()
    {
        // 剩 4.9GB：低于告警阈值 5GB，但远高于 512MB 拒收门槛，必须照常落盘。
        using var dir = new TempDir();
        var spool = new HistorySpool(dir.Path, minFreeMb: 512, freeBytesProbe: () => (long)(4.9 * 1024 * 1024 * 1024));
        await spool.AppendAsync(Batch(1, 1000), default);
        Assert.Equal(1, spool.SpilledBatches);
        Assert.Equal(0, spool.DroppedBatches);
    }

    [Fact]
    public async Task FreeSpaceBelowMinRejects()
    {
        using var dir = new TempDir();
        var spool = new HistorySpool(dir.Path, minFreeMb: 512, freeBytesProbe: () => 100L * 1024 * 1024);
        await Assert.ThrowsAsync<IOException>(() => spool.AppendAsync(Batch(1, 1000), default));
        Assert.Equal(1, spool.DroppedBatches);
        Assert.Equal(0, spool.SpilledBatches);
    }

    [Fact]
    public async Task MinFreeZeroDisablesDiskCheck()
    {
        using var dir = new TempDir();
        var spool = new HistorySpool(dir.Path, minFreeMb: 0, freeBytesProbe: () => 1L);
        await spool.AppendAsync(Batch(1, 1000), default);
        Assert.Equal(1, spool.SpilledBatches);
    }

    [Fact]
    public void DefaultSettingsSeparateWarnAndRejectThresholds()
    {
        var s = new ScadaSettings();
        Assert.Equal(5, s.DiskFreeWarnGb);
        Assert.Equal(512, s.HistorySpoolMinFreeMb);
        Assert.True(s.HistorySpoolMinFreeMb < s.DiskFreeWarnGb * 1024);
    }

    private static async Task Until(Func<bool> condition, Func<string>? describe = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!condition()) await Task.Delay(25, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"条件未在时限内满足：{describe?.Invoke() ?? "无诊断信息"}");
        }
    }

    private sealed class Snapshot(params Tag[] tags) : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => Array.Empty<Device>();
        public IReadOnlyList<Tag> Tags => tags;
        public IReadOnlyList<AlarmRule> AlarmRules => Array.Empty<AlarmRule>();
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag tag)
        {
            var found = tags.FirstOrDefault(x => x.Id == tagId); tag = found!; return found is not null;
        }
        public bool TryGetDevice(int deviceId, out Device device) { device = null!; return false; }
    }

    private sealed class ObservedBus : IRealtimeBus, IDisposable
    {
        private readonly RealtimeBus _bus = new();
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Backlog => _bus.Backlog; public long DroppedTotal => _bus.DroppedTotal;
        public void Publish(TagValue value) => _bus.Publish(value);
        public System.Threading.Channels.ChannelReader<TagValue> Subscribe() { var reader = _bus.Subscribe(); Subscribed.TrySetResult(); return reader; }
        public void Dispose() => _bus.Dispose();
    }

    private sealed class FlakyStore : IHistoryStore, IHistoryStreamReader
    {
        public Func<CancellationToken, IAsyncEnumerable<HistoryValue>>? ReadValues { get; init; }
        public IAsyncEnumerable<HistoryValue> ReadAsync(IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution, CancellationToken cancellationToken)
            => ReadValues!(cancellationToken);
        private volatile bool _fail;
        private int _attempts;
        public bool Fail { get => _fail; set => _fail = value; }
        public int Attempts => Volatile.Read(ref _attempts);
        public System.Collections.Concurrent.ConcurrentQueue<HistoryValue> Written { get; } = new();
        public Task WriteRawAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            if (Fail) return Task.FromException(new IOException("history db unavailable"));
            foreach (var value in values) Written.Enqueue(value); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<HistorySeries>> QueryRawAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteAggregate5MinAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteAggregateHourAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<HistorySeries>> QueryAggregate5MinAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<HistorySeries>> QueryAggregateHourAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AggregateValue>> QueryRawAggregatesAsync(long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AggregateValue>> Query5MinAggregatesAsync(long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AggregatePoint>> QueryHourAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<AggregatePoint>> QueryAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scada-history-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
        }
    }
}
