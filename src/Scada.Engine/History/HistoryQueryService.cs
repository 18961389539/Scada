using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace Scada.Engine.History;

public sealed record HistoryQueryResult(
    HistoryResolution Resolution,
    IReadOnlyList<HistorySeries> Series,
    bool Downsampled = false,
    int PointLimit = 0,
    IReadOnlyList<HistoryGap>? Gaps = null);

public sealed class HistoryQueryException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class HistoryQueryService : IDisposable
{
    private static readonly TimeSpan RawLimit = TimeSpan.FromHours(6);
    private static readonly TimeSpan FiveMinuteLimit = TimeSpan.FromDays(7);

    private readonly IHistoryStore _store;
    private readonly SemaphoreSlim _slots;
    private readonly ScadaSettings _settings;
    private readonly HistoryLossLog? _loss;
    private long _rejected;
    private long _timedOut;
    private long _readLimited;

    public HistoryQueryService(IHistoryStore store, IOptions<ScadaSettings>? settings = null, HistoryLossLog? loss = null)
    {
        _store = store;
        _settings = settings?.Value ?? new ScadaSettings();
        _loss = loss;
        MaxConcurrent = Math.Clamp(_settings.HistoryQueryMaxConcurrent, 1, 64);
        _slots = new SemaphoreSlim(MaxConcurrent, MaxConcurrent);
    }

    public int MaxConcurrent { get; }
    public int ActiveQueries => MaxConcurrent - _slots.CurrentCount;
    public long RejectedQueries => Interlocked.Read(ref _rejected);
    public long TimedOutQueries => Interlocked.Read(ref _timedOut);
    public long ReadLimitedQueries => Interlocked.Read(ref _readLimited);

    public Task<HistoryQueryResult> QueryAsync(
        IReadOnlyCollection<int> tagIds,
        long from,
        long to,
        CancellationToken cancellationToken = default,
        int maxPoints = 0)
        => ExecuteAsync(token => QueryCoreAsync(tagIds, from, to, token, maxPoints), cancellationToken);

    public Task<IReadOnlyList<HistorySeries>> QueryRawExactAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken = default)
        => ExecuteAsync(async token =>
        {
            if (_store is IHistoryStreamReader reader)
                return await CollectAsync(ReadLimitedAsync(reader.ReadAsync(tagIds, from, to, HistoryResolution.Raw, token),
                    Math.Max(1, _settings.HistoryAnalysisMaxPoints), token), token);
            var series = await _store.QueryRawAsync(tagIds, from, to, token);
            if (series.Sum(x => (long)x.Points.Count) > Math.Max(1, _settings.HistoryAnalysisMaxPoints)) throw ReadLimitExceeded();
            return series;
        }, cancellationToken);

    private async Task<HistoryQueryResult> QueryCoreAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken, int maxPoints)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(to - from, 0));
        var resolution = span <= RawLimit ? HistoryResolution.Raw
            : span <= FiveMinuteLimit ? HistoryResolution.FiveMinute : HistoryResolution.Hour;
        if (_store is IHistoryStreamReader reader)
        {
            var values = ReadLimitedAsync(reader.ReadAsync(tagIds, from, to, resolution, cancellationToken),
                Math.Max(1, _settings.HistoryQueryMaxReadPoints), cancellationToken);
            if (maxPoints <= 0)
                return await FinalizeAsync(new HistoryQueryResult(resolution, await CollectAsync(values, cancellationToken)), tagIds, from, to, cancellationToken);
            var (series, downsampled) = await HistoryDownsampler.ApplyStreamingAsync(
                values, tagIds, from, to, maxPoints, cancellationToken);
            return await FinalizeAsync(new HistoryQueryResult(resolution, series, downsampled, maxPoints), tagIds, from, to, cancellationToken);
        }
        if (span <= RawLimit)
            return await FinalizeAsync(Limit(HistoryResolution.Raw, await _store.QueryRawAsync(tagIds, from, to, cancellationToken), maxPoints), tagIds, from, to, cancellationToken);
        if (span <= FiveMinuteLimit)
            return await FinalizeAsync(Limit(HistoryResolution.FiveMinute, await _store.QueryAggregate5MinAsync(tagIds, from, to, cancellationToken), maxPoints), tagIds, from, to, cancellationToken);
        return await FinalizeAsync(Limit(HistoryResolution.Hour, await _store.QueryAggregateHourAsync(tagIds, from, to, cancellationToken), maxPoints), tagIds, from, to, cancellationToken);
    }

    public Task<IReadOnlyList<HistoryValue>> SnapshotAsync(
        IReadOnlyCollection<int> tagIds, long ts, CancellationToken cancellationToken = default)
        => ExecuteAsync(async token => _store is IHistoryStreamReader reader
            ? await reader.LastBeforeAsync(tagIds, ts, token)
            : Array.Empty<HistoryValue>(), cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _slots.WaitAsync(0, cancellationToken))
        {
            Interlocked.Increment(ref _rejected);
            throw new HistoryQueryException("history_query_busy", "历史查询繁忙，请稍后重试");
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.HistoryQueryTimeoutSeconds, 1, 300)));
            try { return await query(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                Interlocked.Increment(ref _timedOut);
                throw new HistoryQueryException("history_query_timeout", "历史查询超时，请减少测点或调整查询区间");
            }
        }
        finally { _slots.Release(); }
    }

    private async IAsyncEnumerable<HistoryValue> ReadLimitedAsync(IAsyncEnumerable<HistoryValue> values, int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var value in values.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > limit) throw ReadLimitExceeded();
            yield return value;
        }
    }

    private HistoryQueryException ReadLimitExceeded()
    {
        Interlocked.Increment(ref _readLimited);
        return new HistoryQueryException("history_query_read_limit", "历史数据超过安全读取上限，请减少测点或调整查询区间");
    }

    private static async Task<IReadOnlyList<HistorySeries>> CollectAsync(IAsyncEnumerable<HistoryValue> values, CancellationToken cancellationToken)
    {
        var series = new Dictionary<int, HistorySeries>();
        await foreach (var value in values.WithCancellation(cancellationToken))
        {
            if (!series.TryGetValue(value.TagId, out var item)) series[value.TagId] = item = new HistorySeries { TagId = value.TagId };
            item.Points.Add(new HistoryPoint(value.Ts, value.Value, value.Quality, value.Text, value.Simulated));
        }
        return series.Values.OrderBy(x => x.TagId).ToArray();
    }

    public void Dispose() => _slots.Dispose();

    private async Task<HistoryQueryResult> FinalizeAsync(
        HistoryQueryResult result, IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken)
    {
        IReadOnlyList<AggregatePoint>? aggregates = null;
        if (result.Resolution is HistoryResolution.FiveMinute or HistoryResolution.Hour)
            aggregates = await _store.QueryAggregatePointsAsync(tagIds, from, to, result.Resolution, cancellationToken);
        return WithGaps(EnsureSimulatedSeries(result, aggregates), from, to, aggregates);
    }

    private static HistoryQueryResult EnsureSimulatedSeries(HistoryQueryResult result, IReadOnlyList<AggregatePoint>? aggregates)
    {
        if (aggregates is null) return result;
        var series = result.Series.ToList();
        var present = series.Select(x => x.TagId).ToHashSet();
        foreach (var tagId in aggregates.Where(x => x.SimulatedCount > 0).Select(x => x.TagId).Distinct())
        {
            if (present.Add(tagId))
                series.Add(new HistorySeries { TagId = tagId });
        }
        return result with { Series = series.OrderBy(x => x.TagId).ToList() };
    }

    private HistoryQueryResult WithGaps(HistoryQueryResult result, long from, long to, IReadOnlyList<AggregatePoint>? aggregates = null)
    {
        var loss = _loss?.Overlapping(from, to);
        var simulated = aggregates is null
            ? Array.Empty<HistoryGap>()
            : HistoryGapFinder.FromSimulatedAggregates(aggregates, from, to, result.Resolution);
        var simulatedTags = simulated.Select(x => x.TagId).ToHashSet();
        var seriesForMissing = result.Series.Where(s => s.Points.Count > 0 || !simulatedTags.Contains(s.TagId)).ToList();
        var gaps = HistoryGapFinder.Merge(
            HistoryGapFinder.FromSeries(seriesForMissing, from, to, result.Resolution, loss).Concat(simulated));
        return result with { Gaps = gaps };
    }


    private static HistoryQueryResult Limit(HistoryResolution resolution, IReadOnlyList<HistorySeries> series, int maxPoints)
    {
        if (maxPoints <= 0)
        {
            return new HistoryQueryResult(resolution, series);
        }

        var (limited, downsampled) = HistoryDownsampler.Apply(series, maxPoints);
        return new HistoryQueryResult(resolution, limited, downsampled, maxPoints);
    }
}
