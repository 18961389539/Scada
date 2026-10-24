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
        Assert.False(File.Exists(after.FilePath));
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

    private sealed class FlakyStore : IHistoryStore
    {
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
