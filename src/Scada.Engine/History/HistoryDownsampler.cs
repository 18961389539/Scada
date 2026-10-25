using Scada.Core.Models;

namespace Scada.Engine.History;

// 历史查询按点数预算做 min/max 抽稀：每个时间桶保留最小值和最大值，尖峰不会被等距抽样抹掉。
public static class HistoryDownsampler
{
    public const int DefaultMaxPoints = 20_000;

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
