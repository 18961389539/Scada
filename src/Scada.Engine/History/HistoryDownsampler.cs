using Scada.Core.Models;
using Scada.Core.Enums;

namespace Scada.Engine.History;

// 历史查询按点数预算做 min/max 抽稀：每个时间桶保留最小值和最大值，尖峰不会被等距抽样抹掉。
public static class HistoryDownsampler
{
    public const int DefaultMaxPoints = 20_000;

    public static async Task<(IReadOnlyList<HistorySeries> Series, bool Downsampled)> ApplyStreamingAsync(
        IAsyncEnumerable<HistoryValue> values, IReadOnlyCollection<int> tagIds,
        long from, long to, int maxPoints, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = tagIds.Distinct().ToHashSet();
        if (ids.Count == 0 || to < from) return ([], false);
        if (maxPoints / ids.Count < 2) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        var target = maxPoints / ids.Count;
        var accumulators = new Dictionary<int, StreamingSeries>();
        await foreach (var value in values.WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ids.Contains(value.TagId) || value.Ts < from || value.Ts > to) continue;
            if (!accumulators.TryGetValue(value.TagId, out var accumulator))
                accumulators[value.TagId] = accumulator = new StreamingSeries(target, from, to);
            accumulator.Add(new HistoryPoint(value.Ts, value.Value, value.Quality));
        }
        return (accumulators.OrderBy(x => x.Key).Select(x => new HistorySeries { TagId = x.Key, Points = x.Value.Points() }).ToList(),
            accumulators.Values.Any(x => x.Downsampled));
    }

    private sealed class StreamingSeries(int target, long from, long to)
    {
        private readonly int _slots = target >= 5 ? 3 : 2;
        private readonly int _buckets = Math.Max(1, (target - 2) / (target >= 5 ? 3 : 2));
        private readonly HistoryPoint?[] _min = new HistoryPoint?[Math.Max(1, (target - 2) / (target >= 5 ? 3 : 2))];
        private readonly HistoryPoint?[] _max = new HistoryPoint?[Math.Max(1, (target - 2) / (target >= 5 ? 3 : 2))];
        private readonly HistoryPoint?[] _bad = new HistoryPoint?[Math.Max(1, (target - 2) / (target >= 5 ? 3 : 2))];
        private readonly List<HistoryPoint> _exact = [];
        private HistoryPoint? _first, _last;
        public bool Downsampled { get; private set; }

        public void Add(HistoryPoint point)
        {
            if (_first is null || point.Ts < _first.Ts) _first = point;
            if (_last is null || point.Ts > _last.Ts) _last = point;
            if (!Downsampled)
            {
                if (_exact.Count < target) _exact.Add(point);
                else { Downsampled = true; _exact.Clear(); }
            }
            var bucket = to <= from ? 0 : (int)Math.Clamp((point.Ts - (double)from) / (to - (double)from) * _buckets, 0, _buckets - 1);
            if (_min[bucket] is not { } minimum || point.Value < minimum.Value) _min[bucket] = point;
            if (_max[bucket] is not { } maximum || point.Value > maximum.Value) _max[bucket] = point;
            if (point.Quality != TagQuality.Good) _bad[bucket] ??= point;
        }

        public List<HistoryPoint> Points()
        {
            if (!Downsampled) return _exact.OrderBy(x => x.Ts).ToList();
            if (target < 4) return new[] { _first!, _last! }.DistinctBy(x => x.Ts).ToList();
            var selected = _min.Concat(_max).Concat(_slots == 3 ? _bad : []).Append(_first).Append(_last);
            return selected.OfType<HistoryPoint>().DistinctBy(x => x.Ts).OrderBy(x => x.Ts).ToList();
        }
    }

    public static (IReadOnlyList<HistorySeries> Series, bool Downsampled) Apply(
        IReadOnlyList<HistorySeries> series,
        int maxPoints)
    {
        if (maxPoints <= 0 || series.Count == 0)
        {
            return (series, false);
        }

        var total = 0;
        foreach (var item in series)
        {
            total += item.Points.Count;
            if (total > maxPoints) break;
        }

        if (total <= maxPoints)
        {
            return (series, false);
        }

        var perSeries = Math.Max(2, maxPoints / series.Count);
        var output = new List<HistorySeries>(series.Count);
        foreach (var item in series)
        {
            output.Add(item.Points.Count <= perSeries
                ? item
                : new HistorySeries { TagId = item.TagId, Points = Downsample(item.Points, perSeries) });
        }

        return (output, true);
    }

    private static List<HistoryPoint> Downsample(List<HistoryPoint> points, int target)
    {
        var last = points.Count - 1;
        if (target <= 2)
        {
            return [points[0], points[last]];
        }

        var buckets = Math.Max(1, (target - 2) / 2);
        var firstTs = points[0].Ts;
        var width = Math.Max(1L, (points[last].Ts - firstTs) / buckets);
        var minIndex = new int[buckets];
        var maxIndex = new int[buckets];
        Array.Fill(minIndex, -1);
        Array.Fill(maxIndex, -1);

        for (var i = 1; i < last; i++)
        {
            var bucket = (int)Math.Clamp((points[i].Ts - firstTs) / width, 0, buckets - 1);
            if (minIndex[bucket] < 0 || points[i].Value < points[minIndex[bucket]].Value) minIndex[bucket] = i;
            if (maxIndex[bucket] < 0 || points[i].Value > points[maxIndex[bucket]].Value) maxIndex[bucket] = i;
        }

        var selected = new SortedSet<int> { 0, last };
        foreach (var index in minIndex) if (index > 0) selected.Add(index);
        foreach (var index in maxIndex) if (index > 0) selected.Add(index);
        return selected.Select(index => points[index]).ToList();
    }
}
