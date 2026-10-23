using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Engine.Reports;

public sealed record ReportPoint(long Ts, double Avg, double Min, double Max, int Count);

public sealed class ReportSeries
{
    public int TagId { get; init; }
    public List<ReportPoint> Points { get; init; } = new();
}

public sealed record ReportResult(string Type, long From, long To, IReadOnlyList<ReportSeries> Series);

public sealed class ReportService
{
    private readonly IHistoryStore _store;

    public ReportService(IHistoryStore store)
    {
        _store = store;
    }

    public async Task<ReportResult> BuildAsync(
        string type,
        IReadOnlyCollection<int> tagIds,
        long from,
        long to,
        CancellationToken cancellationToken = default)
    {
        var rows = (await _store.QueryHourAggregatePointsAsync(tagIds, from, to, cancellationToken)).ToList();
        rows.AddRange(await BuildRecentPartialHoursAsync(tagIds, from, to, rows, cancellationToken));
        var seriesMap = rows
            .GroupBy(x => x.TagId)
            .ToDictionary(
                g => g.Key,
                g => new ReportSeries { TagId = g.Key, Points = BuildPoints(type, g) });

        return new ReportResult(type, from, to, seriesMap.Values.OrderBy(x => x.TagId).ToList());
    }

    /// <summary>
    /// 当前小时尚未生成聚合记录，用原始数据补算最近两小时，保证当天报表有数据。
    /// </summary>
    private async Task<IReadOnlyList<AggregatePoint>> BuildRecentPartialHoursAsync(
        IReadOnlyCollection<int> tagIds,
        long from,
        long to,
        IReadOnlyCollection<AggregatePoint> existing,
        CancellationToken cancellationToken)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowEnd = Math.Min(to, nowMs);
        var fallbackFrom = Math.Max(from, AlignToHour(windowEnd) - 3600_000L);
        if (fallbackFrom >= windowEnd)
        {
            return Array.Empty<AggregatePoint>();
        }

        var covered = existing.Select(x => (x.TagId, x.Ts)).ToHashSet();
        var raw = await _store.QueryRawAsync(tagIds, fallbackFrom, windowEnd, cancellationToken);
        var points = new List<AggregatePoint>();
        foreach (var series in raw)
        {
            foreach (var group in series.Points
                         .Where(x => x.Quality == TagQuality.Good)
                         .GroupBy(x => AlignToHour(x.Ts)))
            {
                if (covered.Contains((series.TagId, group.Key)))
                {
                    continue;
                }

                points.Add(new AggregatePoint(
                    series.TagId,
                    group.Key,
                    group.Average(x => x.Value),
                    group.Min(x => x.Value),
                    group.Max(x => x.Value),
                    group.Count()));
            }
        }

        return points;
    }

    private static long AlignToHour(long ts)
    {
        var value = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
    }

    private static List<ReportPoint> BuildPoints(string type, IEnumerable<AggregatePoint> rows)
    {
        if (!string.Equals(type, "monthly", StringComparison.OrdinalIgnoreCase))
        {
            return rows
                .OrderBy(x => x.Ts)
                .Select(x => new ReportPoint(x.Ts, x.Avg, x.Min, x.Max, x.Count))
                .ToList();
        }

        return rows
            .GroupBy(x => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(x.Ts).ToLocalTime().DateTime))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var count = g.Sum(x => x.Count);
                var avg = count == 0 ? 0 : g.Sum(x => x.Avg * x.Count) / count;
                var date = new DateTime(g.Key.Year, g.Key.Month, g.Key.Day, 0, 0, 0, DateTimeKind.Local);
                var ts = new DateTimeOffset(date).ToUnixTimeMilliseconds();
                return new ReportPoint(ts, avg, g.Min(x => x.Min), g.Max(x => x.Max), count);
            })
            .ToList();
    }
}
