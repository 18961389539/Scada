using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Engine.History;

// 历史区间分析：采样值按“保持到下一次采样”处理，只有 Good 质量进入时间加权均值/积分/极值，
// 其余质量单独形成区间，便于现场区分“没有数据”和“数据不可信”。
public static class HistoryAnalysis
{
    public const int MaxQualityIntervals = 2000;

    public static IReadOnlyList<HistoryTagAnalysis> Analyze(
        IReadOnlyList<HistorySeries> series,
        long from,
        long to,
        int maxIntervals = MaxQualityIntervals)
    {
        if (to <= from) return [];
        return series.Select(item => AnalyzeTag(item, from, to, maxIntervals)).ToList();
    }

    private static HistoryTagAnalysis AnalyzeTag(HistorySeries series, long from, long to, int maxIntervals)
    {
        var points = series.Points.Where(p => p.Ts >= from && p.Ts <= to).OrderBy(p => p.Ts).ToList();
        var span = to - from;
        if (points.Count == 0)
        {
            return new HistoryTagAnalysis(series.TagId, 0, 0, span, 0, 0, null, null, null, 0, [], false);
        }

        var intervals = new List<HistoryQualityInterval>();
        var weighted = 0d;
        var goodMs = 0L;
        var goodSamples = 0;
        double? min = null;
        double? max = null;

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var end = i + 1 < points.Count ? points[i + 1].Ts : to;
            var duration = end - point.Ts;
            if (duration <= 0) continue;

            if (intervals.Count > 0 && intervals[^1].Quality == point.Quality && intervals[^1].To == point.Ts)
            {
                intervals[^1] = intervals[^1] with { To = end };
            }
            else
            {
                intervals.Add(new HistoryQualityInterval(point.Quality, point.Ts, end));
            }

            if (point.Quality != TagQuality.Good) continue;
            weighted += point.Value * duration;
            goodMs += duration;
            goodSamples++;
            min = min is double lo ? Math.Min(lo, point.Value) : point.Value;
            max = max is double hi ? Math.Max(hi, point.Value) : point.Value;
        }

        var truncated = intervals.Count > maxIntervals;
        if (truncated) intervals = intervals.Take(maxIntervals).ToList();

        return new HistoryTagAnalysis(
            series.TagId,
            points.Count,
            goodSamples,
            span,
            goodMs,
            Math.Round(goodMs * 100d / span, 2),
            goodMs == 0 ? null : Math.Round(weighted / goodMs, 4),
            min is double minValue ? Math.Round(minValue, 4) : null,
            max is double maxValue ? Math.Round(maxValue, 4) : null,
            Math.Round(weighted / 3_600_000d, 4),
            intervals,
            truncated);
    }
}
