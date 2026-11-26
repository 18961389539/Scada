using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Engine.History;

public static class HistoryGapFinder
{
    public static IReadOnlyList<HistoryGap> FromSeries(
        IEnumerable<HistorySeries> series, long from, long to, HistoryResolution resolution,
        IEnumerable<HistoryGap>? loss = null)
    {
        var gaps = new List<HistoryGap>();
        var minHole = resolution switch
        {
            HistoryResolution.FiveMinute => 2 * 5 * 60_000L,
            HistoryResolution.Hour => 2 * 3_600_000L,
            _ => 0L
        };

        foreach (var item in series)
        {
            var points = item.Points.OrderBy(x => x.Ts).ToList();
            var hole = minHole;
            if (hole == 0 && points.Count >= 3)
            {
                var deltas = new List<long>();
                for (var i = 1; i < points.Count; i++)
                {
                    var delta = points[i].Ts - points[i - 1].Ts;
                    if (delta > 0) deltas.Add(delta);
                }
                deltas.Sort();
                if (deltas.Count > 0)
                    hole = Math.Max(2_000L, deltas[deltas.Count / 2] * 3);
            }
            else if (hole == 0) hole = 15_000L;

            if (points.Count == 0)
            {
                if (to > from)
                    gaps.Add(new HistoryGap(item.TagId, from, to, HistoryGapKind.Missing, "查询区间内没有采样"));
                continue;
            }

            if (points[0].Ts - from > hole)
                gaps.Add(new HistoryGap(item.TagId, from, points[0].Ts, HistoryGapKind.Missing, null));

            HistoryQualityInterval? bad = null;
            (long From, long To)? sim = null;
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                var end = i + 1 < points.Count ? points[i + 1].Ts : to;
                if (point.Simulated)
                {
                    if (sim is { } s && s.To == point.Ts) sim = (s.From, end);
                    else { FlushSim(); sim = (point.Ts, end); }
                    FlushBad();
                }
                else
                {
                    FlushSim();
                    if (point.Quality != TagQuality.Good)
                    {
                        if (bad is not null && bad.To == point.Ts && bad.Quality == point.Quality)
                            bad = bad with { To = end };
                        else
                        {
                            FlushBad();
                            bad = new HistoryQualityInterval(point.Quality, point.Ts, end);
                        }
                    }
                    else FlushBad();
                }

                if (i + 1 < points.Count && points[i + 1].Ts - point.Ts > hole)
                    gaps.Add(new HistoryGap(item.TagId, point.Ts, points[i + 1].Ts, HistoryGapKind.Missing, null));
            }
            FlushBad();
            FlushSim();
            if (to - points[^1].Ts > hole)
                gaps.Add(new HistoryGap(item.TagId, points[^1].Ts, to, HistoryGapKind.Missing, null));

            void FlushBad()
            {
                if (bad is null) return;
                gaps.Add(new HistoryGap(item.TagId, bad.From, bad.To, HistoryGapKind.BadQuality, bad.Quality.ToString()));
                bad = null;
            }

            void FlushSim()
            {
                if (sim is not { } s) return;
                gaps.Add(new HistoryGap(item.TagId, s.From, s.To, HistoryGapKind.Simulated, "仿真采样"));
                sim = null;
            }
        }

        if (loss is not null)
        {
            foreach (var window in loss)
            {
                if (window.To < from || window.From > to) continue;
                gaps.Add(window with
                {
                    From = Math.Max(window.From, from),
                    To = Math.Min(window.To, to)
                });
            }
        }

        return Merge(gaps);
    }

    /// <summary>
    /// 聚合窗口上的仿真计数：仅仿真窗口不输出数值点，由这里生成仿真缺口。
    /// </summary>
    public static IReadOnlyList<HistoryGap> FromSimulatedAggregates(
        IEnumerable<AggregatePoint> points, long from, long to, HistoryResolution resolution)
    {
        var window = resolution switch
        {
            HistoryResolution.Hour => 3_600_000L,
            HistoryResolution.FiveMinute => 5 * 60_000L,
            _ => 0L
        };
        if (window <= 0) return [];
        var gaps = new List<HistoryGap>();
        foreach (var point in points)
        {
            if (point.SimulatedCount <= 0) continue;
            var start = Math.Max(point.Ts, from);
            var end = Math.Min(point.Ts + window, to);
            if (end > start)
                gaps.Add(new HistoryGap(point.TagId, start, end, HistoryGapKind.Simulated, "仿真采样"));
        }
        return Merge(gaps);
    }

    public static IReadOnlyList<HistoryGap> Merge(IEnumerable<HistoryGap> gaps) =>
        gaps.Where(x => x.To > x.From)
            .OrderBy(x => x.TagId ?? 0).ThenBy(x => x.From).ThenBy(x => x.Kind)
            .ToArray();
}
