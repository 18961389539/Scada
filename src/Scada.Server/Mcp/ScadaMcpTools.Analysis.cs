using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Engine.Alarms;
using Scada.Engine.History;
using Scada.Server.Endpoints;

namespace Scada.Server.Mcp;

public sealed partial class ScadaMcpTools
{
    [McpServerTool(Name = "analyze_alarms", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read alarm counts, priority distribution, frequent sources and hourly/daily trend for [from,to), at most 31 days. Optional area/device filters. Truncated and rollup flags must be reported with conclusions; no thresholds or alarms are modified.")]
    public Task<McpAlarmAnalysis> AnalyzeAlarms(long from, long to, CancellationToken cancellationToken,
        int? areaId = null, int? deviceId = null, int top = 10) =>
        queryBudget.RunAsync(async ct =>
        {
            ValidateWindow(from, to);
            if (top is < 1 or > 20) throw new McpToolException(McpErrorCodes.InvalidArgument, "top must be between 1 and 20.");
            await using var db = await factory.CreateDbContextAsync(ct);
            if (areaId is not null && (areaId <= 0 || !await db.Areas.AnyAsync(x => x.Id == areaId, ct)))
                throw new McpToolException(McpErrorCodes.NotFound, "Area does not exist.");
            if (deviceId is not null && (deviceId <= 0 || !await db.Devices.AnyAsync(x => x.Id == deviceId, ct)))
                throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
            var access = new AccessContext(null, [new AccessGrant(Permission.View, null)], await accessModels.GetAsync(ct));
            var areaFilter = AlarmAnalyticsEndpoints.Include(access, areaId, null, null);
            var deviceTags = deviceId is null ? [] : await db.Tags.Where(x => x.DeviceId == deviceId).Select(x => x.Id).ToArrayAsync(ct);
            var tags = deviceTags.ToHashSet();
            bool Include(AlarmSample sample) => areaFilter(sample) &&
                (deviceId is null || sample.DeviceId == deviceId || sample.TagId is int id && tags.Contains(id));
            var thresholds = (await alarmAnalytics.GetThresholdsAsync(ct)).Thresholds.Clone();
            thresholds.TopN = top;
            var now = ScadaTime.NowMs();
            var report = await alarmAnalytics.AnalyzeAsync(from, to, Include, now, thresholds, ct);
            var trend = await alarmAnalytics.TrendAsync(from, to, Include, now, ct);
            var kpi = report.Kpi;
            return new McpAlarmAnalysis(from, to, report.DetailFrom, report.UsedRollups, report.Truncated,
                kpi.Total, kpi.Annunciated, kpi.Suppressed, kpi.Chattering, kpi.Rating,
                kpi.AvgAckSec, kpi.AvgClearSec, kpi.Priority, kpi.Top.Take(top).ToArray(), trend.Granularity, trend.Points,
                ScadaTime.NowMs());
        }, cancellationToken);

    [McpServerTool(Name = "analyze_history", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Analyze exact raw history for 1-10 tags over at most 24 hours: time-weighted average, min/max, quality coverage and optional below/above-limit duration. Uses Good samples held until the next sample, with no interpolation or extrapolation before the first sample. Never analyzes downsampled data.")]
    public Task<McpHistoryAnalysis> AnalyzeHistory(int[] tagIds, long from, long to,
        CancellationToken cancellationToken, double? lowerLimit = null, double? upperLimit = null) =>
        queryBudget.RunAsync(async ct =>
        {
            var ids = ValidateIds(tagIds, 10);
            ValidateWindow(from, to, 1);
            if (lowerLimit is double low && !double.IsFinite(low) || upperLimit is double high && !double.IsFinite(high)
                || lowerLimit > upperLimit)
                throw new McpToolException(McpErrorCodes.InvalidArgument,
                    "Limits must be finite and lowerLimit must not exceed upperLimit.");
            await RequireTagsAsync(ids, ct);
            var raw = await history.QueryRawExactAsync(ids, from, to, ct);
            var byId = raw.ToDictionary(x => x.TagId);
            var series = ids.Select(id => byId.GetValueOrDefault(id) ?? new HistorySeries { TagId = id }).ToArray();
            if (series.Any(s => s.Points.Any(p => p.Quality == TagQuality.Good && !double.IsFinite(p.Value))))
                throw new McpToolException(McpErrorCodes.DataIntegrity,
                    "Raw history contains a non-finite Good value; statistics cannot be computed reliably.");
            var analyses = HistoryAnalysis.Analyze(series, from, to, maxIntervals: 100).ToDictionary(x => x.TagId);
            var tags = series.Select(s =>
            {
                var points = s.Points.Where(p => p.Ts >= from && p.Ts <= to).OrderBy(p => p.Ts).ToArray();
                long below = 0, above = 0;
                for (var i = 0; i < points.Length; i++)
                {
                    if (points[i].Quality != TagQuality.Good) continue;
                    var end = i + 1 < points.Length ? points[i + 1].Ts : to;
                    var duration = Math.Max(0, end - points[i].Ts);
                    if (points[i].Value < lowerLimit) below += duration;
                    if (points[i].Value > upperLimit) above += duration;
                }
                return new McpTagAnalysis(analyses[s.TagId], lowerLimit is null ? null : below, upperLimit is null ? null : above);
            }).ToArray();
            return new McpHistoryAnalysis(from, to, lowerLimit, upperLimit, tags,
                ScadaTime.NowMs(), tags.Any(x => x.Statistics.QualityTruncated));
        }, cancellationToken);

    [McpServerTool(Name = "query_aggregates", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read stored 5-minute or hourly aggregate buckets (avg/min/max/count) for 1-10 configured tags: FiveMinute up to 7 days, Hour up to 366 days. No raw fallback and no recent-raw fill; missing buckets are not zero. maxPoints (20-2000) is the total bucket budget; larger requests are rejected instead of truncated. Uses the analysis query budget.")]
    public Task<McpAggregateResult> QueryAggregates(int[] tagIds, long from, long to,
        [Description("Stored resolution: FiveMinute or Hour. Raw is not available.")] HistoryResolution resolution,
        CancellationToken cancellationToken, int maxPoints = 500) =>
        queryBudget.RunAsync(async ct =>
        {
            var ids = ValidateIds(tagIds, 10);
            if (resolution is not (HistoryResolution.FiveMinute or HistoryResolution.Hour))
                throw new McpToolException(McpErrorCodes.InvalidArgument,
                    "resolution must be FiveMinute or Hour; raw history is not an aggregate.");
            ValidateRange(from, to);
            var days = resolution == HistoryResolution.FiveMinute ? 7 : 366;
            if (from == to || to - from > (long)TimeSpan.FromDays(days).TotalMilliseconds
                || to > DateTimeOffset.MaxValue.AddDays(-2).ToUnixTimeMilliseconds())
                throw new McpToolException(McpErrorCodes.InvalidArgument,
                    $"Aggregate window must be non-empty and no longer than {days} days for {resolution}.");
            if (maxPoints is < 20 or > 2000)
                throw new McpToolException(McpErrorCodes.InvalidArgument, "maxPoints must be between 20 and 2000.");
            var step = resolution == HistoryResolution.FiveMinute ? 5 * 60_000L : 3_600_000L;
            var expected = (long)ids.Length * ((to - from) / step + 2);
            if (expected > maxPoints)
                throw new McpToolException(McpErrorCodes.InvalidArgument,
                    $"Up to {expected} aggregate buckets exceed maxPoints {maxPoints}. Narrow the range, use Hour or reduce tagIds.");
            await RequireTagsAsync(ids, ct);
            var points = await historyStore.QueryAggregatePointsAsync(ids, from, to, resolution, ct);
            var byTag = points.GroupBy(x => x.TagId).ToDictionary(g => g.Key, g => g.ToArray());
            var series = ids.Where(byTag.ContainsKey).Select(id => new McpAggregateSeries(id,
                byTag[id].Select(x => new McpAggregatePoint(x.Ts, x.Avg, x.Min, x.Max, x.Count)).ToArray())).ToArray();
            return new McpAggregateResult(resolution.ToString(), series,
                ids.Where(id => !byTag.ContainsKey(id)).ToArray(), maxPoints, ScadaTime.NowMs(), false);
        }, cancellationToken);

    [McpServerTool(Name = "get_collector_diagnostics", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read an existing local collector diagnostic snapshot with paged point quality and at most 50 recent events/samples. Does not probe, reconnect or issue gateway engineering commands. Remote-gateway devices without a local snapshot return an explicit error. Connection strings and device credentials are omitted.")]
    public async Task<McpDiagnostics> GetCollectorDiagnostics(int deviceId, CancellationToken cancellationToken,
        int page = 1, int limit = 50, bool staleOnly = false)
    {
        if (page is < 1 or > 100)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "page must be between 1 and 100.");
        ValidatePage(0, limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null) throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
        if (device.GatewayId is not null)
            throw new McpToolException(McpErrorCodes.Unsupported,
                "Remote gateway diagnostics are not cached locally. MCP will not issue gateway engineering commands.");
        var snapshot = diagnostics.GetDiagnostics(deviceId)
            ?? throw new McpToolException(McpErrorCodes.DataUnavailable,
                "No collector diagnostic snapshot is available for this device.", true);
        var result = CollectorDiagnosticPaging.Page(snapshot, new CollectorDiagnosticQuery(page, limit, StaleOnly: staleOnly));
        return new(snapshot.DeviceId, snapshot.Name, snapshot.State, snapshot.CurrentError, snapshot.CapturedTs,
            snapshot.ConfigurationPending, snapshot.Quality, snapshot.Metrics, result.Diagnostics.Points,
            snapshot.Events.OrderByDescending(x => x.Ts).ThenByDescending(x => x.Id).Take(50).ToArray(),
            snapshot.Samples.OrderByDescending(x => x.Ts).Take(50).ToArray(),
            result.Total, page, limit, snapshot.Events.Count > 50 || snapshot.Samples.Count > 50, snapshot.CapturedTs);
    }

    private static void ValidateWindow(long from, long to, int days = 31)
    {
        ValidateRange(from, to);
        if (from == to || to - from > (long)TimeSpan.FromDays(days).TotalMilliseconds
            || to > DateTimeOffset.MaxValue.AddDays(-2).ToUnixTimeMilliseconds())
            throw new McpToolException(McpErrorCodes.InvalidArgument,
                $"Time window must be non-empty, within the supported calendar, and no longer than {days} days.");
    }

    public sealed record McpAlarmAnalysis(long From, long To, long DetailFrom, bool UsedRollups, bool Truncated,
        int Total, int Annunciated, int Suppressed, int Chattering, string Rating, double? AvgAckSec, double? AvgClearSec,
        IReadOnlyList<AlarmPriorityItem> Priority, IReadOnlyList<AlarmBadActor> Top, string Granularity,
        IReadOnlyList<AlarmTrendPoint> Trend, long DataAsOf);
    public sealed record McpTagAnalysis(HistoryTagAnalysis Statistics, long? BelowLimitMs, long? AboveLimitMs);
    public sealed record McpHistoryAnalysis(long From, long To, double? LowerLimit, double? UpperLimit,
        IReadOnlyList<McpTagAnalysis> Tags, long DataAsOf, bool Truncated);
    public sealed record McpAggregatePoint(long Ts, double Avg, double Min, double Max, int Count);
    public sealed record McpAggregateSeries(int TagId, IReadOnlyList<McpAggregatePoint> Points);
    public sealed record McpAggregateResult(string Resolution, IReadOnlyList<McpAggregateSeries> Series,
        int[] MissingTagIds, int PointBudget, long DataAsOf, bool Truncated);
    public sealed record McpDiagnostics(int DeviceId, string Name, string State, string? Error, long CapturedTs,
        bool ConfigurationPending, CollectorQualityCounts Quality, CollectorDiagnosticMetrics Metrics,
        IReadOnlyList<CollectorTagDiagnostic> Points, IReadOnlyList<CollectorDiagnosticEvent> Events, IReadOnlyList<CollectorPollSample> Samples,
        int TotalPoints, int Page, int Limit, bool Truncated, long DataAsOf);
}
