using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Calculations;

namespace Scada.Engine.Alarms;

public sealed record AlarmTrendPoint(long Ts, int Total, int Annunciated, int Info, int Warning, int Critical);

// 分析结果 + 数据来源说明：DetailFrom 之后用原始明细（抖动/洪水/关联/中位数等），之前用预聚合（只有计数、均值类指标）。
public sealed record AlarmAnalyticsReport(
    AlarmAnalyticsResult Kpi, long DetailFrom, bool UsedRollups, bool Truncated,
    IReadOnlyList<AlarmRateBucket> Rate10, IReadOnlyList<AlarmSuggestion> Suggestions, AlarmAnalyticsThresholds Thresholds);

public sealed record AlarmEventPage(int Total, IReadOnlyList<AlarmSample> Items);

// 报警统计分析服务：取数（原始明细 + 预聚合）、阈值配置、下钻明细。include 由调用方提供，包含区域授权、区域/等级/规则过滤。
public sealed class AlarmAnalyticsService(IDbContextFactory<ConfigDbContext> factory, IOptions<ScadaSettings> options)
{
    public const long DayMs = 86_400_000L;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ScadaSettings _settings = options.Value;

    public TimeZoneInfo Zone => CalcJobEngine.ResolveZone(_settings.ShiftTimeZone);

    public AlarmAnalyticsThresholds Defaults() => new()
    {
        FloodStartPer10Min = Math.Max(_settings.AlarmFloodThreshold, 1),
        FloodEndPer10Min = Math.Max(1, Math.Min(Math.Max(_settings.AlarmFloodThreshold, 1), Math.Max(1, _settings.AlarmFloodThreshold / 2))),
        ChatterCount = Math.Max(_settings.AlarmChatterCount, 2),
        ChatterWindowSec = Math.Max(_settings.AlarmChatterWindowSec, 1),
        StandingHours = Math.Max(_settings.AlarmStandingHours, 1),
        StaleHours = Math.Max(_settings.AlarmStandingHours, 1)
    };

    public async Task<(AlarmAnalyticsThresholds Thresholds, long Revision)> GetThresholdsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AlarmAnalyticsConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        return (Parse(row?.ThresholdsJson), row?.Revision ?? 0);
    }

    public AlarmAnalyticsThresholds Parse(string? json)
    {
        var result = Defaults();
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return result;
        try
        {
            // 以默认值为底，覆盖已保存的字段；保存后又非法（例如手工改库）时回退默认值。
            using var doc = JsonDocument.Parse(json);
            var merged = JsonSerializer.SerializeToNode(result, Json)!.AsObject();
            foreach (var p in doc.RootElement.EnumerateObject())
                if (merged.ContainsKey(p.Name)) merged[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
            var parsed = merged.Deserialize<AlarmAnalyticsThresholds>(Json)!;
            return parsed.Validate() is null ? parsed : result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return result; }
    }

    public static string Serialize(AlarmAnalyticsThresholds thresholds) => JsonSerializer.Serialize(thresholds, Json);

    public long DayStart(long ts)
    {
        var zone = Zone;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ts), zone).DateTime.Date;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }

    public long NextDay(long dayStart) => DayStart(dayStart + DayMs + 2 * AlarmAnalytics.HourMs) is var d && d > dayStart ? d : dayStart + DayMs;

    public static long FloorHour(long ts) => (long)Math.Floor(ts / (double)AlarmAnalytics.HourMs) * AlarmAnalytics.HourMs;

    private static IQueryable<AlarmSample> Project(IQueryable<AlarmEvent> q) =>
        q.Select(e => new AlarmSample(e.Id, e.RuleId, e.TagId, e.DeviceId, e.GatewayId, e.Level, e.StartTs, e.EndTs, e.AckTs, e.AckUser, e.Shelved, e.Suppressed, e.Chattering, e.Message));

    // 开始于 [from,to) 的报警 + 开始更早、在 from 时仍活动的报警（驻留/抑制时长需要）。
    public async Task<(List<AlarmSample> Samples, bool Truncated)> LoadSamplesAsync(long from, long to, Func<AlarmSample, bool> include, CancellationToken ct)
    {
        var max = Math.Max(1000, _settings.AlarmAnalyticsMaxRows);
        await using var db = await factory.CreateDbContextAsync(ct);
        var inWindow = await Project(db.AlarmEvents.AsNoTracking().Where(x => x.StartTs >= from && x.StartTs < to).OrderByDescending(x => x.StartTs)).Take(max + 1).ToListAsync(ct);
        var truncated = inWindow.Count > max;
        if (truncated) inWindow.RemoveAt(inWindow.Count - 1);
        var carried = await Project(db.AlarmEvents.AsNoTracking().Where(x => x.StartTs < from && (x.EndTs == null || x.EndTs > from))).Take(50_000).ToListAsync(ct);
        var all = inWindow.Concat(carried).Where(include).OrderBy(x => x.StartTs).ThenBy(x => x.Id).ToList();
        return (all, truncated);
    }

    public async Task<List<ShelveInterval>> LoadShelvesAsync(long from, long asOf, Func<AlarmSample, bool> include, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.AlarmShelves.AsNoTracking().Where(x => x.StartTs < asOf && (x.ClearedTs ?? x.UntilTs) > from)
            .Join(db.AlarmRules.AsNoTracking(), s => s.RuleId, r => r.Id, (s, r) => new { s.RuleId, s.StartTs, s.UntilTs, s.ClearedTs, r.TagId, r.Level, Name = r.Message })
            .ToListAsync(ct);
        return rows.Where(x => include(new AlarmSample(0, x.RuleId, x.TagId, null, null, x.Level, x.StartTs, null, null, null, true, false, false, x.Name ?? "")))
            .Select(x => new ShelveInterval(x.RuleId, x.StartTs, Math.Min(x.ClearedTs ?? x.UntilTs, x.UntilTs))).ToList();
    }

    public async Task<Dictionary<int, AlarmRuleConfig>> LoadRulesAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AlarmRules.AsNoTracking().ToDictionaryAsync(r => r.Id, r => new AlarmRuleConfig(r.Id, r.Message ?? r.Key, r.Level, r.DelaySec, r.OffDelaySec, r.RecoverDeadband), ct);
    }

    public static AlarmSample FromRollup(AlarmRollup r) => new(0, r.RuleId, r.TagId, r.DeviceId, r.GatewayId, r.Level, r.BucketTs, null, null, null, false, false, false, r.Message);

    public async Task<long?> WatermarkAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.AlarmRollupStates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct))?.WatermarkTs;
    }

    // 区间 [from,to) 的预聚合行（小时粒度优先；超出小时保留期的部分用日粒度）；预聚合尚未覆盖的部分用原始数据现算。
    public async Task<List<AlarmRollup>> RollupRowsAsync(long from, long to, Func<AlarmSample, bool> include, AlarmAnalyticsThresholds th, long now, CancellationToken ct)
    {
        var rows = new List<AlarmRollup>();
        if (to <= from) return rows;
        var watermark = await WatermarkAsync(ct) ?? from;
        var rolledTo = Math.Clamp(FloorHour(watermark), from, to);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (rolledTo > from)
        {
            var hourlyFrom = Math.Max(from, FloorHour(now - _settings.AlarmRollupHourlyRetentionDays * DayMs) + AlarmAnalytics.HourMs);
            if (hourlyFrom > from)
            {
                // 小时数据已过保留期：用日粒度（按本地日对齐，起点所在日整日计入）。
                var dayFrom = DayStart(from);
                var dayTo = DayStart(Math.Min(hourlyFrom, rolledTo));
                rows.AddRange(await db.AlarmRollups.AsNoTracking().Where(x => x.Granularity == AlarmRollup.Day && x.BucketTs >= dayFrom && x.BucketTs < dayTo).ToListAsync(ct));
                hourlyFrom = Math.Max(dayTo, from);
            }
            var hf = FloorHour(hourlyFrom);
            rows.AddRange(await db.AlarmRollups.AsNoTracking().Where(x => x.Granularity == AlarmRollup.Hour && x.BucketTs >= hf && x.BucketTs < rolledTo).ToListAsync(ct));
        }
        if (rolledTo < to)
        {
            var (samples, _) = await LoadSamplesAsync(rolledTo, to, include, ct);
            rows.AddRange(AlarmAnalytics.Rollup(samples, rolledTo, to, AlarmRollup.Hour, FloorHour, th));
        }
        return rows.Where(r => include(FromRollup(r))).ToList();
    }

    public async Task<AlarmAnalyticsReport> AnalyzeAsync(long from, long to, Func<AlarmSample, bool> include, long now, AlarmAnalyticsThresholds? thresholds = null, CancellationToken ct = default)
    {
        var th = thresholds ?? (await GetThresholdsAsync(ct)).Thresholds;
        if (to <= from) to = from + 1;
        var rawMax = Math.Max(1, _settings.AlarmAnalyticsRawMaxDays) * DayMs;
        var detailFrom = to - from > rawMax ? FloorHour(to - rawMax) : from;
        var (samples, truncated) = await LoadSamplesAsync(detailFrom, to, include, ct);
        var shelves = await LoadShelvesAsync(from, Math.Min(to, now), include, ct);
        var kpi = AlarmAnalytics.Analyze(samples, shelves, detailFrom, to, now, th);
        var usedRollups = false;
        if (detailFrom > from)
        {
            usedRollups = true;
            var rollups = await RollupRowsAsync(from, detailFrom, include, th, now, ct);
            kpi = Merge(kpi, rollups, from, to, now, th, shelves);
        }
        var rules = await LoadRulesAsync(ct);
        var suggestions = AlarmAnalytics.Suggest(kpi, samples, rules, th);
        var rate10 = AlarmAnalytics.Buckets10(samples, detailFrom, to);
        return new AlarmAnalyticsReport(kpi, detailFrom, usedRollups, truncated, rate10, suggestions, th);
    }

    // 长区间：把预聚合覆盖的较早部分并入计数/比例/均值类指标；峰值、洪水、中位数、抖动/关联只来自明细窗口。
    public static AlarmAnalyticsResult Merge(AlarmAnalyticsResult detail, IReadOnlyList<AlarmRollup> rollups, long from, long to, long now, AlarmAnalyticsThresholds th, IReadOnlyList<ShelveInterval> shelves)
    {
        var hours = (to - from) / (double)AlarmAnalytics.HourMs;
        var total = detail.Total + rollups.Sum(r => r.Count);
        var ann = detail.Annunciated + rollups.Sum(r => r.Annunciated);
        var positions = detail.OperatorPositions;
        var per10 = ann / ((to - from) / (double)AlarmAnalytics.Bucket10) / positions;
        double R(double v, int d = 2) => Math.Round(v, d, MidpointRounding.AwayFromZero);
        var ackCount = detail.AckCount + rollups.Sum(r => r.AckCount);
        var ackSum = (detail.AvgAckSec ?? 0) * detail.AckCount + rollups.Sum(r => r.AckSumMs) / 1000d;
        var clearCount = detail.ClearCount + rollups.Sum(r => r.ClearCount);
        var clearSum = (detail.AvgClearSec ?? 0) * detail.ClearCount + rollups.Sum(r => r.ClearSumMs) / 1000d;
        var annTotal = Math.Max(1, ann);
        var priority = detail.Priority.Select(p =>
        {
            var c = p.Count + rollups.Where(r => r.Level.ToString() == p.Level).Sum(r => r.Annunciated);
            return p with { Count = c, Percent = R(ann == 0 ? 0 : c * 100d / annTotal) };
        }).ToList();
        // Top N：明细中的坏分子 + 预聚合计数按源合并后重新排序。
        var bySource = new Dictionary<string, (AlarmBadActor Actor, int Count, int Ann, int Fleeting)>(StringComparer.Ordinal);
        foreach (var r in rollups)
        {
            var key = r.Source;
            var actor = bySource.TryGetValue(key, out var e) ? e.Actor : new AlarmBadActor(r.Source, r.RuleId, r.TagId, r.DeviceId, r.Message, r.Level.ToString(), 0, 0, 0, 0, 0, 0);
            bySource[key] = (actor, (e.Actor is null ? 0 : e.Count) + r.Count, (e.Actor is null ? 0 : e.Ann) + r.Annunciated, (e.Actor is null ? 0 : e.Fleeting) + r.Fleeting);
        }
        var merged = new List<AlarmBadActor>();
        foreach (var t in detail.Top)
        {
            var extra = bySource.Remove(t.Source, out var e) ? e : default;
            merged.Add(t with { Count = t.Count + extra.Count, Annunciated = t.Annunciated + extra.Ann, Fleeting = t.Fleeting + extra.Fleeting });
        }
        merged.AddRange(bySource.Values.Select(v => v.Actor with { Count = v.Count, Annunciated = v.Ann, Fleeting = v.Fleeting }));
        double cumulative = 0; var allCount = Math.Max(1, total);
        var top = merged.OrderByDescending(x => x.Count).ThenBy(x => x.Message, StringComparer.Ordinal).Take(th.TopN)
            .Select(x => { var pct = x.Count * 100d / allCount; cumulative += pct; return x with { Percent = R(pct), CumulativePercent = R(cumulative) }; }).ToList();
        var asOf = Math.Min(to, now);
        long Overlap(long a0, long a1) => Math.Max(0, Math.Min(a1, asOf) - Math.Max(a0, from));
        var shelveCount = shelves.Count(x => x.StartTs < asOf && x.EndTs > from);
        var shelvedHours = R(shelves.Sum(x => Overlap(x.StartTs, x.EndTs)) / (double)AlarmAnalytics.HourMs);
        var shelvedByRule = shelves.Where(x => x.StartTs < asOf && x.EndTs > from).GroupBy(x => x.RuleId)
            .Select(g => new AlarmShelveStat(g.Key, g.Count(), R(g.Sum(x => Overlap(x.StartTs, x.EndTs)) / (double)AlarmAnalytics.HourMs)))
            .OrderByDescending(x => x.Hours).ThenBy(x => x.RuleId).ToList();
        return detail with
        {
            From = from, Hours = R(hours, 3),
            Total = total, Annunciated = ann,
            Shelved = detail.Shelved + rollups.Sum(r => r.Shelved), Suppressed = detail.Suppressed + rollups.Sum(r => r.Suppressed), Chattering = detail.Chattering + rollups.Sum(r => r.Chattering),
            PerOperator10Min = R(per10, 3), PerOperatorHour = R(ann / hours / positions), PerOperatorDay = R(ann / hours * 24 / positions), Rating = AlarmAnalytics.RateRating(per10, th),
            AckCount = ackCount, AvgAckSec = ackCount == 0 ? null : R(ackSum / ackCount, 1),
            ClearCount = clearCount, AvgClearSec = clearCount == 0 ? null : R(clearSum / clearCount, 1),
            Priority = priority, Top = top,
            FleetingTotal = detail.FleetingTotal + rollups.Sum(r => r.Fleeting),
            ShelveCount = shelveCount, ShelvedHours = shelvedHours, ShelvedByRule = shelvedByRule
        };
    }

    // 趋势：≤ 2 天按小时，否则按本地日。原始明细可覆盖时直接现算，否则用预聚合。
    public async Task<(string Granularity, List<AlarmTrendPoint> Points)> TrendAsync(long from, long to, Func<AlarmSample, bool> include, long now, CancellationToken ct = default)
    {
        if (to <= from) to = from + 1;
        var hourly = to - from <= 2 * DayMs;
        Func<long, long> bucketOf = hourly ? FloorHour : DayStart;
        var th = (await GetThresholdsAsync(ct)).Thresholds;
        List<AlarmRollup> rows;
        if (to - from <= Math.Max(1, _settings.AlarmAnalyticsRawMaxDays) * DayMs)
        {
            var (samples, _) = await LoadSamplesAsync(from, to, include, ct);
            rows = AlarmAnalytics.Rollup(samples, from, to, hourly ? AlarmRollup.Hour : AlarmRollup.Day, bucketOf, th);
        }
        else rows = await RollupRowsAsync(from, to, include, th, now, ct);
        var points = new List<AlarmTrendPoint>();
        var grouped = rows.GroupBy(r => bucketOf(r.BucketTs)).ToDictionary(g => g.Key, g => g.ToList());
        for (var b = bucketOf(from); b < to && points.Count < 2000; b = hourly ? b + AlarmAnalytics.HourMs : NextDay(b))
        {
            var g = grouped.GetValueOrDefault(b) ?? [];
            points.Add(new AlarmTrendPoint(b, g.Sum(r => r.Count), g.Sum(r => r.Annunciated),
                g.Where(r => r.Level == AlarmLevel.Info).Sum(r => r.Annunciated), g.Where(r => r.Level == AlarmLevel.Warning).Sum(r => r.Annunciated), g.Where(r => r.Level == AlarmLevel.Critical).Sum(r => r.Annunciated)));
        }
        return (hourly ? "hour" : "day", points);
    }

    // 下钻：开始于 [from,to) 的报警明细，可按报警源、种类（annunciated/fleeting/chattering/shelved/suppressed）过滤；ids 指定时只取这些报警。
    public async Task<AlarmEventPage> EventsAsync(long from, long to, Func<AlarmSample, bool> include, string? source, string? kind, IReadOnlyCollection<int>? ids, int limit, CancellationToken ct = default)
    {
        var th = (await GetThresholdsAsync(ct)).Thresholds;
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.AlarmEvents.AsNoTracking().Where(x => x.StartTs >= from && x.StartTs < to);
        if (ids is { Count: > 0 }) q = q.Where(x => ids.Contains(x.Id));
        var rows = (await Project(q.OrderByDescending(x => x.StartTs)).Take(Math.Max(1000, _settings.AlarmAnalyticsMaxRows)).ToListAsync(ct)).Where(include);
        if (!string.IsNullOrEmpty(source)) rows = rows.Where(x => x.Source == source);
        rows = kind switch
        {
            "annunciated" => rows.Where(x => x.Annunciated),
            "fleeting" => rows.Where(x => AlarmAnalytics.IsFleeting(x, th)),
            "chattering" => rows.Where(x => x.Chattering),
            "shelved" => rows.Where(x => x.Shelved),
            "suppressed" => rows.Where(x => x.Suppressed),
            _ => rows
        };
        var list = rows.ToList();
        return new AlarmEventPage(list.Count, list.Take(Math.Clamp(limit, 1, 2000)).ToList());
    }
}

// 报警预聚合后台任务：每 AlarmRollupIntervalSec 秒汇总到最近整点；首次回补 AlarmRollupBackfillDays 天；每轮重算最近
// AlarmRollupRecomputeHours 小时（迟到的确认/恢复）；按保留期清理。备机不运行。
public sealed class AlarmRollupService(IServiceProvider services, IDbContextFactory<ConfigDbContext> factory, IOptions<ScadaSettings> options, ILogger<AlarmRollupService> logger) : BackgroundService
{
    private readonly ScadaSettings _settings = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<int> RunOnceAsync(long now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await RunCoreAsync(now, ct); }
        finally { _gate.Release(); }
    }

    private async Task<int> RunCoreAsync(long now, CancellationToken ct)
    {
        var analytics = services.GetRequiredService<AlarmAnalyticsService>();
        var th = (await analytics.GetThresholdsAsync(ct)).Thresholds;
        await using var db = await factory.CreateDbContextAsync(ct);
        var state = await db.AlarmRollupStates.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (state is null) { state = new AlarmRollupState { Id = 1 }; db.AlarmRollupStates.Add(state); }
        var closed = AlarmAnalyticsService.FloorHour(now);
        long start;
        if (state.WatermarkTs is long w) start = Math.Max(0, w - Math.Max(1, _settings.AlarmRollupRecomputeHours) * AlarmAnalytics.HourMs);
        else
        {
            var earliest = await db.AlarmEvents.AsNoTracking().MinAsync(x => (long?)x.StartTs, ct) ?? closed;
            start = Math.Max(earliest, closed - Math.Max(1, _settings.AlarmRollupBackfillDays) * AlarmAnalyticsService.DayMs);
        }
        if (state.RebuildFromTs is long rebuild) start = Math.Min(start, rebuild);
        var written = 0;
        // 按本地日处理：重算该日内到 closed 为止的小时行与日行（日行含当天已结束的部分小时）。
        for (var day = analytics.DayStart(start); day < closed; day = analytics.NextDay(day))
        {
            ct.ThrowIfCancellationRequested();
            var dayEnd = Math.Min(analytics.NextDay(day), closed);
            var hourFrom = Math.Max(day, AlarmAnalyticsService.FloorHour(start));
            var samples = await db.AlarmEvents.AsNoTracking().Where(x => x.StartTs >= day && x.StartTs < dayEnd)
                .Select(e => new AlarmSample(e.Id, e.RuleId, e.TagId, e.DeviceId, e.GatewayId, e.Level, e.StartTs, e.EndTs, e.AckTs, e.AckUser, e.Shelved, e.Suppressed, e.Chattering, e.Message))
                .ToListAsync(ct);
            var hourly = AlarmAnalytics.Rollup(samples, hourFrom, dayEnd, AlarmRollup.Hour, AlarmAnalyticsService.FloorHour, th);
            var daily = AlarmAnalytics.Rollup(samples, day, dayEnd, AlarmRollup.Day, _ => day, th);
            await db.AlarmRollups.Where(x => x.Granularity == AlarmRollup.Hour && x.BucketTs >= hourFrom && x.BucketTs < dayEnd).ExecuteDeleteAsync(ct);
            await db.AlarmRollups.Where(x => x.Granularity == AlarmRollup.Day && x.BucketTs == day).ExecuteDeleteAsync(ct);
            db.AlarmRollups.AddRange(hourly); db.AlarmRollups.AddRange(daily);
            written += hourly.Count + daily.Count;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            db.Attach(state);
        }
        // 保留期清理。
        var hourCut = closed - Math.Max(1, _settings.AlarmRollupHourlyRetentionDays) * AlarmAnalyticsService.DayMs;
        var dayCut = closed - Math.Max(1, _settings.AlarmRollupDailyRetentionDays) * AlarmAnalyticsService.DayMs;
        await db.AlarmRollups.Where(x => x.Granularity == AlarmRollup.Hour && x.BucketTs < hourCut).ExecuteDeleteAsync(ct);
        await db.AlarmRollups.Where(x => x.Granularity == AlarmRollup.Day && x.BucketTs < dayCut).ExecuteDeleteAsync(ct);
        state.WatermarkTs = closed; state.LastRunTs = now; state.RebuildFromTs = null; state.LastError = null;
        await db.SaveChangesAsync(ct);
        return written;
    }

    // 请求重算（阈值变更或手动回补）：下一轮从 fromTs 起重算。
    public async Task RequestRebuildAsync(long fromTs, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var state = await db.AlarmRollupStates.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (state is null) { state = new AlarmRollupState { Id = 1 }; db.AlarmRollupStates.Add(state); }
        state.RebuildFromTs = state.RebuildFromTs is long r ? Math.Min(r, fromTs) : fromTs;
        await db.SaveChangesAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.AlarmRollupEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ContinueWith(_ => { });
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _settings.AlarmRollupIntervalSec)));
        do
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (services.GetService<INodeRole>() is { IsActive: false }) continue;
            try { await RunOnceAsync(ScadaTime.NowMs(), stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "报警预聚合失败");
                try
                {
                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var state = await db.AlarmRollupStates.FirstOrDefaultAsync(x => x.Id == 1, stoppingToken);
                    if (state is not null) { state.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message; await db.SaveChangesAsync(stoppingToken); }
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested) { }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => !t.IsCanceled && t.Result));
    }
}
