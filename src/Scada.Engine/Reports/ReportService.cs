using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.Calculations;
using Microsoft.Extensions.Options;

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
    private readonly IOptions<ScadaSettings>? _options;

    public ReportService(IHistoryStore store, IOptions<ScadaSettings>? options = null)
    {
        _store = store;
        _options = options;
    }

    private bool ExplicitZone => !string.IsNullOrWhiteSpace(_options?.Value.ShiftTimeZone);

    // 月报按工厂时区（ShiftTimeZone）切天，与报表中心、OEE、报警统计一致
    public TimeZoneInfo Zone => CalcJobEngine.ResolveZone(_options?.Value.ShiftTimeZone);

    // 小时聚合（含尚未生成聚合的最近小时，用原始数据补算）。
    public async Task<IReadOnlyList<AggregatePoint>> HourlyAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken = default)
    {
        var rows = (await _store.QueryHourAggregatePointsAsync(tagIds, from, to, cancellationToken)).ToList();
        rows.AddRange(await BuildRecentPartialHoursAsync(tagIds, from, to, rows, cancellationToken));
        return rows;
    }

    public Task<IReadOnlyList<HistorySeries>> RawAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken = default) =>
        _store.QueryRawAsync(tagIds, from, to, cancellationToken);

    public async Task<ReportResult> BuildAsync(
        string type,
        IReadOnlyCollection<int> tagIds,
        long from,
        long to,
        CancellationToken cancellationToken = default)
    {
        var rows = (await _store.QueryHourAggregatePointsAsync(tagIds, from, to, cancellationToken)).ToList();
        rows.AddRange(await BuildRecentPartialHoursAsync(tagIds, from, to, rows, cancellationToken));
        return BuildResult(type, from, to, rows);
    }

    public async Task<ReportResult> BuildStoredAsync(string type, IReadOnlyCollection<int> tagIds,
        long from, long to, CancellationToken cancellationToken = default) =>
        BuildResult(type, from, to, await _store.QueryHourAggregatePointsAsync(tagIds, from, to, cancellationToken));

    private ReportResult BuildResult(string type, long from, long to, IReadOnlyList<AggregatePoint> rows)
    {
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
            foreach (var group in series.Points.GroupBy(x => AlignToHour(x.Ts)))
            {
                if (covered.Contains((series.TagId, group.Key)))
                {
                    continue;
                }

                var live = group.Where(x => x.Quality == TagQuality.Good && !x.Simulated && double.IsFinite(x.Value)).ToList();
                var simulatedCount = group.Count(x => x.Simulated);
                if (live.Count == 0 && simulatedCount == 0) continue;
                points.Add(new AggregatePoint(
                    series.TagId,
                    group.Key,
                    live.Count == 0 ? 0 : live.Average(x => x.Value),
                    live.Count == 0 ? 0 : live.Min(x => x.Value),
                    live.Count == 0 ? 0 : live.Max(x => x.Value),
                    live.Count,
                    simulatedCount));
            }
        }

        return points;
    }

    // 未配置工厂时区时仍按 UTC 整点，与历史库里已有的小时键一致。
    // 配置之后按该时区的本地整点，和聚合触发器使用的基准偏移是同一网格。
    private long AlignToHour(long ts) => ZoneTime.FloorHour(ts, ExplicitZone ? Zone : TimeZoneInfo.Utc);

    private List<ReportPoint> BuildPoints(string type, IEnumerable<AggregatePoint> rows)
    {
        if (!string.Equals(type, "monthly", StringComparison.OrdinalIgnoreCase))
        {
            return rows
                .OrderBy(x => x.Ts)
                .Select(x => new ReportPoint(x.Ts, x.Avg, x.Min, x.Max, x.Count))
                .ToList();
        }

        return rows
            .GroupBy(x => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(x.Ts), Zone).Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var count = g.Sum(x => x.Count);
                var avg = count == 0 ? 0 : g.Sum(x => x.Avg * x.Count) / count;
                var ts = ReportPeriods.ToUtc(g.Key, Zone);
                return new ReportPoint(ts, avg, g.Min(x => x.Min), g.Max(x => x.Max), count);
            })
            .ToList();
    }
}
