using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data.Infrastructure;
using Scada.Engine.History;
using Xunit.Abstractions;

namespace Scada.Engine.Tests;

public sealed class CapacityBaselineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OpenHistoryReaderDoesNotBlockArchiving()
    {
        var root = Path.Combine(Path.GetTempPath(), "scada-reader-writer-" + Guid.NewGuid().ToString("N"));
        var settings = Options.Create(new ScadaSettings { HistoryRoot = root });
        var factory = new HistoryDbFactory(settings);
        var store = new HistoryStore(factory);
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            await store.WriteRawAsync([new(1, start, 1, TagQuality.Good)], default);
            Task write;
            Task completed;
            await using (var reader = store.ReadAsync([1], start, start + 1, HistoryResolution.Raw, default).GetAsyncEnumerator())
            {
                Assert.True(await reader.MoveNextAsync());
                write = Task.Run(() => store.WriteRawAsync([new(1, start + 1, 2, TagQuality.Good)], default));
                completed = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(3)));
            }
            await write;
            Assert.Same(write, completed);
            Assert.Equal(2, Assert.Single(await store.QueryRawAsync([1], start, start + 1, default)).Points.Count);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.db")) factory.ReleaseFile(file);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public async Task ConcurrentArchivingAndTrendsRemainBoundedAndReplayWithoutDuplicates()
    {
        var tags = Setting("SCADA_CAPACITY_TAGS", 100, 1, 10000);
        var samples = Setting("SCADA_CAPACITY_SAMPLES", 100, 10, 10000);
        var readers = Setting("SCADA_CAPACITY_READERS", 2, 1, 4);
        var p95BudgetMs = Setting("SCADA_CAPACITY_P95_MS", 10000, 1, 60000);
        var memoryBudgetMb = Setting("SCADA_CAPACITY_MEMORY_MB", 512, 16, 8192);
        var root = Path.Combine(Path.GetTempPath(), "scada-capacity-" + Guid.NewGuid().ToString("N"));
        var settings = Options.Create(new ScadaSettings { HistoryRoot = root, HistoryQueryMaxConcurrent = readers,
            HistoryQueryMaxReadPoints = 2000000, HistoryQueryTimeoutSeconds = 60 });
        var factory = new HistoryDbFactory(settings);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = deadline.Token;
        var store = new HistoryStore(factory);
        using var query = new HistoryQueryService(store, settings);
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var queryIds = Enumerable.Range(1, Math.Min(tags, 50)).ToArray();
        HistoryValue[] Batch(int sample) => Enumerable.Range(1, tags)
            .Select(id => new HistoryValue(id, start + sample * 1000L, sample, TagQuality.Good)).ToArray();
        var latency = new ConcurrentBag<double>();
        var writeLatency = new ConcurrentBag<double>();
        var peakMemory = Process.GetCurrentProcess().WorkingSet64;
        var initialMemory = peakMemory;
        try
        {
            await store.WriteRawAsync(Batch(0), token);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = Stopwatch.StartNew();
            var archiveMs = 0d;
            var writer = Task.Run(async () =>
            {
                await ready.Task;
                var archiveWatch = Stopwatch.StartNew();
                for (var sample = 1; sample < samples; sample++)
                {
                    var write = Stopwatch.StartNew();
                    await store.WriteRawAsync(Batch(sample), token);
                    writeLatency.Add(write.Elapsed.TotalMilliseconds);
                    await Task.Yield();
                }
                archiveMs = archiveWatch.Elapsed.TotalMilliseconds;
            }, token);
            var queries = Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
            {
                await ready.Task;
                var completed = 0;
                do
                {
                    var timer = Stopwatch.StartNew();
                    var result = await query.QueryAsync(queryIds, start, start + (samples - 1) * 1000L, token, 20000);
                    latency.Add(timer.Elapsed.TotalMilliseconds);
                    completed++;
                    Assert.InRange(result.Series.Sum(series => series.Points.Count), 1, 20000);
                    var memory = Process.GetCurrentProcess().WorkingSet64;
                    long current;
                    do { current = Interlocked.Read(ref peakMemory); }
                    while (memory > current && Interlocked.CompareExchange(ref peakMemory, memory, current) != current);
                    await Task.Delay(10, token);
                } while (!writer.IsCompleted || completed < 20);
            }, token)).ToArray();
            ready.SetResult();
            await Task.WhenAll(queries.Append(writer));
            watch.Stop();

            var spool = new HistorySpool(root);
            await spool.AppendAsync(Batch(samples - 1), token);
            await Assert.ThrowsAsync<IOException>(() => spool.DrainAsync(
                _ => Task.FromException(new IOException("Injected storage outage")), token));
            Assert.True(spool.PendingBytes > 0);
            var restarted = new HistoryStore(new HistoryDbFactory(settings));
            await new HistorySpool(root).DrainAsync(batch => restarted.WriteRawAsync(batch, token), token);
            Assert.Equal(0, new HistorySpool(root).PendingBytes);
            var read = await restarted.QueryRawAsync([1], start, start + (samples - 1) * 1000L, token);
            Assert.Equal(samples, Assert.Single(read).Points.Count);
            Assert.Equal(samples, read[0].Points.Select(point => point.Ts).Distinct().Count());
            Assert.Equal(0, query.ActiveQueries);
            var ordered = latency.Order().ToArray();
            var p95 = ordered[(int)Math.Ceiling(ordered.Length * .95) - 1];
            var memoryGrowth = Math.Max(0, peakMemory - initialMemory);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                tags, samples, readers, queries = ordered.Length, elapsedMs = watch.Elapsed.TotalMilliseconds,
                archiveMs, archivedSamplesPerSecond = tags * (samples - 1) / (archiveMs / 1000),
                queryP95Ms = p95, maxArchiveBatchMs = writeLatency.Max(),
                workingSetGrowthBytes = memoryGrowth, p95BudgetMs, memoryBudgetMb,
                replay = "no-loss-or-duplicates-for-persisted-batch"
            }));
            Assert.True(p95 <= p95BudgetMs, $"Query P95 {p95:F1} ms exceeds {p95BudgetMs} ms");
            Assert.True(memoryGrowth <= memoryBudgetMb * 1024L * 1024L,
                $"Working set growth {memoryGrowth} bytes exceeds {memoryBudgetMb} MiB");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.db")) factory.ReleaseFile(file);
                Directory.Delete(root, true);
            }
        }
    }

    private static int Setting(string name, int fallback, int minimum, int maximum)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (text is null) return fallback;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new ArgumentException($"{name} must be an integer between {minimum} and {maximum}");
        return value;
    }
}
