using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Calculations;

namespace Scada.Engine.Oee;

public sealed record OeeReport(
    OeeKpiResult Kpi, long DetailFrom, bool UsedRollups,
    IReadOnlyList<OeeBreakdownRow> Trend, OeeThresholds Thresholds);

// OEE 查询与配置：班次展开、停机/产量装载、短区间现算、长区间走预聚合。
public sealed class OeeService(IDbContextFactory<ConfigDbContext> factory, IOptions<ScadaSettings> options)
{
    public const long DayMs = OeeMath.DayMs;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ScadaSettings _settings = options.Value;

    public TimeZoneInfo Zone => CalcJobEngine.ResolveZone(_settings.ShiftTimeZone);

    public OeeThresholds Defaults() => new();

    public async Task<(OeeThresholds Thresholds, long Revision)> GetThresholdsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.OeeConfigs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        return (Parse(row?.ThresholdsJson), row?.Revision ?? 0);
    }

    public OeeThresholds Parse(string? json)
    {
        var result = Defaults();
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var merged = JsonSerializer.SerializeToNode(result, Json)!.AsObject();
            foreach (var p in doc.RootElement.EnumerateObject())
                if (merged.ContainsKey(p.Name)) merged[p.Name] = System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText());
            var parsed = merged.Deserialize<OeeThresholds>(Json)!;
            return parsed.Validate() is null ? parsed : result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return result; }
    }

    public static string Serialize(OeeThresholds t) => JsonSerializer.Serialize(t, Json);

    public long DayStart(long ts)
    {
        var zone = Zone;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ts), zone).DateTime.Date;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }

    public long NextDay(long dayStart)
    {
        var n = DayStart(dayStart + DayMs + 2 * OeeMath.HourMs);
        return n > dayStart ? n : dayStart + DayMs;
    }

    public static long FloorMinute(long ts) => ts / OeeMath.MinuteMs * OeeMath.MinuteMs;
    public static long FloorHour(long ts) => ts / OeeMath.HourMs * OeeMath.HourMs;

    // 展开 [from,to) 内落在指定区域的班次窗口（支持跨零点）。
    public List<OeeShiftWindow> ExpandShifts(IReadOnlyList<ProductionShift> shifts, long from, long to)
    {
        var zone = Zone;
        var result = new List<OeeShiftWindow>();
        if (to <= from || shifts.Count == 0) return result;
        var cursor = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(from - DayMs), zone).Date;
        var endLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(to + DayMs), zone).Date;
        for (var day = cursor; day <= endLocal; day = day.AddDays(1))
        {
            var dow = (int)day.DayOfWeek; // 0=Sun
            foreach (var s in shifts.Where(x => x.Enabled && (x.Days & (1 << dow)) != 0))
            {
                var startLocal = day.AddMinutes(s.StartMinute);
                var endLocalT = s.EndMinute > s.StartMinute ? day.AddMinutes(s.EndMinute) : day.AddDays(1).AddMinutes(s.EndMinute);
                var start = new DateTimeOffset(DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified), zone.GetUtcOffset(startLocal)).ToUnixTimeMilliseconds();
                var end = new DateTimeOffset(DateTime.SpecifyKind(endLocalT, DateTimeKind.Unspecified), zone.GetUtcOffset(endLocalT)).ToUnixTimeMilliseconds();
                var a = Math.Max(start, from); var b = Math.Min(end, to);
                if (b > a) result.Add(new OeeShiftWindow(a, b, s.Id, s.Name));
            }
        }
        return result.OrderBy(x => x.StartTs).ToList();
    }

    public static LineState MapState(string stateMapJson, double? value)
    {
        if (value is null || !double.IsFinite(value.Value)) return LineState.Unknown;
        var key = Math.Abs(value.Value - Math.Round(value.Value)) < 1e-9 ? ((long)Math.Round(value.Value)).ToString() : value.Value.ToString("G");
        try
        {
            if (!string.IsNullOrWhiteSpace(stateMapJson) && stateMapJson != "{}")
            {
                using var doc = JsonDocument.Parse(stateMapJson);
                if (doc.RootElement.TryGetProperty(key, out var mapped) && mapped.GetString() is string name
                    && Enum.TryParse<LineState>(name, true, out var st)) return st;
            }
        }
        catch (JsonException) { }
        // 缺省：非 0 → Running，0 → Stopped。
        return Math.Abs(value.Value) < 1e-9 ? LineState.Stopped : LineState.Running;
    }

    public static OeeStateKind ToKind(LineState state, bool planned) => state switch
    {
        LineState.Running => OeeStateKind.Running,
        LineState.Fault => OeeStateKind.Fault,
        LineState.Setup => OeeStateKind.Setup,
        LineState.Stopped when planned => OeeStateKind.PlannedDown,
        LineState.Stopped => OeeStateKind.Stopped,
        _ => OeeStateKind.Stopped
    };

    // 停机事件 → 区间；无事件的空隙视为 Running（半开）。
    public static List<OeeInterval> BuildIntervals(long from, long to, IReadOnlyList<DowntimeEvent> events, IReadOnlyDictionary<int, DowntimeReason> reasons)
    {
        var clipped = events
            .Where(e => e.Confirmed && e.StartTs < to && (e.EndTs ?? long.MaxValue) > from)
            .OrderBy(e => e.StartTs).ThenBy(e => e.Id)
            .Select(e =>
            {
                reasons.TryGetValue(e.ReasonId ?? -1, out var r);
                var planned = r?.IsPlanned == true || (r?.Category == OeeLossCategory.Availability && r.IsPlanned);
                var kind = r?.IsPlanned == true ? OeeStateKind.PlannedDown : ToKind(e.State, false);
                if (r?.Category == OeeLossCategory.Performance && e.State != LineState.Fault) kind = OeeStateKind.Setup;
                return new OeeInterval(Math.Max(e.StartTs, from), Math.Min(e.EndTs ?? to, to), kind, e.ReasonId, r?.Code, r?.Name ?? e.State.ToString(), r?.IsPlanned == true);
            })
            .Where(i => i.EndTs > i.StartTs)
            .ToList();

        var result = new List<OeeInterval>();
        var cursor = from;
        foreach (var iv in clipped)
        {
            if (iv.StartTs > cursor) result.Add(new OeeInterval(cursor, iv.StartTs, OeeStateKind.Running));
            result.Add(iv);
            cursor = Math.Max(cursor, iv.EndTs);
        }
        if (cursor < to) result.Add(new OeeInterval(cursor, to, OeeStateKind.Running));
        return result;
    }

    public async Task<OeeReport> AnalyzeAsync(long from, long to, IReadOnlyList<int> lineIds, int? productId, int? shiftId, long now, OeeThresholds th, CancellationToken ct)
    {
        if (to <= from) throw new ArgumentException("empty range");
        await using var db = await factory.CreateDbContextAsync(ct);
        var lines = await db.ProductionLines.AsNoTracking().Where(x => lineIds.Contains(x.Id)).ToListAsync(ct);
        if (lines.Count == 0)
            return new OeeReport(Empty(from, to, now), from, false, [], th);

        var areaIds = lines.Select(l => l.AreaId).Distinct().ToList();
        var shifts = await db.ProductionShifts.AsNoTracking().Where(x => x.Enabled && areaIds.Contains(x.AreaId)).ToListAsync(ct);
        if (shiftId is int sid) shifts = shifts.Where(s => s.Id == sid).ToList();
        var windows = ExpandShifts(shifts, from, to);

        var reasons = await db.DowntimeReasons.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var events = await db.DowntimeEvents.AsNoTracking()
            .Where(x => lineIds.Contains(x.LineId) && x.StartTs < to && (x.EndTs == null || x.EndTs > from))
            .ToListAsync(ct);

        var products = await db.ProductionProducts.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var corrections = await db.ProductionCountCorrections.AsNoTracking()
            .Where(x => lineIds.Contains(x.LineId) && x.PeriodFrom < to && x.PeriodTo > from)
            .ToListAsync(ct);
        if (productId is int pid) corrections = corrections.Where(c => c.ProductId == pid).ToList();

        // 短区间：逐产线现算后合并；长区间：预聚合 + 最近 rawMax 天现算。
        var rawMax = Math.Max(1, _settings.OeeAnalyticsRawMaxDays) * DayMs;
        var detailFrom = to - from <= rawMax ? from : Math.Max(from, to - rawMax);
        var usedRollups = detailFrom > from;

        OeeKpiResult Merge(IEnumerable<OeeKpiResult> parts)
        {
            var list = parts.ToList();
            if (list.Count == 0) return Empty(from, to, now);
            if (list.Count == 1) return list[0];
            // 多产线：汇总时间与产量，重新算 A/P/Q。
            long planned = 0, run = 0, down = 0, pdown = 0, fault = 0, setup = 0, outside = 0;
            double good = 0, reject = 0, scrap = 0, target = 0, idealW = 0, idealT = 0;
            var pareto = new Dictionary<string, OeeDowntimeItem>();
            var faults = new List<OeeFaultItem>();
            foreach (var p in list)
            {
                planned += p.PlannedMs; run += p.RunMs; down += p.DownMs; pdown += p.PlannedDownMs; fault += p.FaultMs; setup += p.SetupMs; outside += p.OutsideMs;
                good += p.Good; reject += p.Reject; scrap += p.Scrap; target += p.Target;
                if (p.IdealCycleSec is > 0 && p.RunMs > 0) { idealW += p.IdealCycleSec.Value * p.RunMs; idealT += p.RunMs; }
                foreach (var d in p.DowntimePareto)
                {
                    var key = d.ReasonId?.ToString() ?? d.Code;
                    if (!pareto.TryGetValue(key, out var cur)) pareto[key] = d;
                    else pareto[key] = d with { Ms = cur.Ms + d.Ms, Hours = cur.Hours + d.Hours, Count = cur.Count + d.Count };
                }
                faults.AddRange(p.Faults);
            }
            var total = good + reject + scrap;
            var ideal = idealT > 0 ? idealW / idealT : (double?)null;
            var avail = planned > 0 ? run / (double)planned : (double?)null;
            double? perf = null; var over = false; var clamped = false;
            if (run > 0 && ideal is > 0 && total > 0)
            {
                perf = (ideal.Value * total) / (run / 1000.0);
                if (perf > 1) { over = true; if (th.ClampPerformance) { perf = 1; clamped = true; } }
            }
            else if (run > 0 && total == 0) perf = 0;
            var quality = total > 0 ? good / total : (double?)null;
            double? oee = avail is double a && perf is double p0 && quality is double q ? a * p0 * q : null;
            var downTotal = pareto.Values.Sum(x => x.Ms);
            var paretoList = pareto.Values.OrderByDescending(x => x.Ms).Take(th.TopN).Select((x, i) =>
            {
                var pct = downTotal > 0 ? x.Ms * 100.0 / downTotal : 0;
                var cum = pareto.Values.OrderByDescending(y => y.Ms).Take(i + 1).Sum(y => y.Ms) * 100.0 / Math.Max(1, downTotal);
                return x with { Percent = Math.Round(pct, 2), CumulativePercent = Math.Round(cum, 2) };
            }).ToList();
            double? mtbf = faults.Count > 0 && run > 0 ? run / (double)faults.Count / OeeMath.HourMs : null;
            double? mttr = faults.Count > 0 ? faults.Average(f => f.Ms) / OeeMath.HourMs : null;
            return new OeeKpiResult(from, to, now, (to - from) / (double)OeeMath.HourMs,
                planned, run, down, pdown, fault, setup, outside, good, reject, scrap, total,
                avail, perf, quality, oee, ideal, target, target > 0 ? total / target * 100 : 0,
                total > 0 ? (reject + scrap) / total : 0, mtbf, mttr, faults.Count, over, clamped,
                paretoList, faults, list.SelectMany(x => x.ByShift).ToList(), list.SelectMany(x => x.ByProduct).ToList(),
                list.SelectMany(x => x.ByDay).GroupBy(d => d.From).Select(g => SumDay(g.ToList(), th)).ToList());
        }

        OeeBreakdownRow SumDay(List<OeeBreakdownRow> rows, OeeThresholds t)
        {
            if (rows.Count == 1) return rows[0];
            return OeeMath.FromRollups(rows[0].From, rows[0].To, rows[0].Label,
                rows.Select(r => (r.PlannedMs, r.RunMs, r.DownMs, r.PlannedDownMs, r.FaultMs, r.SetupMs, r.Good, r.Reject, r.Scrap, r.IdealCycleSec ?? 0, r.Target)), t);
        }

        var parts = new List<OeeKpiResult>();
        foreach (var line in lines)
        {
            var lineEvents = events.Where(e => e.LineId == line.Id).ToList();
            var intervals = BuildIntervals(detailFrom, to, lineEvents, reasons);
            var lineCorrections = corrections.Where(c => c.LineId == line.Id).ToList();
            var counts = new List<OeeCountSlice>();
            foreach (var c in lineCorrections)
            {
                var ideal = line.ProductId is int p0 && products.TryGetValue(c.ProductId ?? p0, out var prod)
                    ? OeeMath.EffectiveIdealCycleSec(prod.RatedUnitsPerHour, prod.IdealCycleSec)
                    : OeeMath.EffectiveIdealCycleSec(line.RatedUnitsPerHour, line.IdealCycleSec);
                if (c.ProductId is int cp && products.TryGetValue(cp, out var p1))
                    ideal = OeeMath.EffectiveIdealCycleSec(p1.RatedUnitsPerHour, p1.IdealCycleSec);
                var target = line.TargetUnitsPerHour ?? 0;
                counts.Add(new OeeCountSlice(c.PeriodFrom, c.PeriodTo, c.GoodDelta, c.RejectDelta, c.ScrapDelta, ideal, target, c.ProductId,
                    c.ProductId is int id && products.TryGetValue(id, out var pn) ? pn.Name : null));
            }
            // 预聚合覆盖 detailFrom 之前的部分。
            if (usedRollups)
            {
                var rollups = await db.OeeRollups.AsNoTracking()
                    .Where(x => x.LineId == line.Id && x.Granularity == OeeRollup.Hour && x.BucketTs >= from && x.BucketTs < detailFrom)
                    .ToListAsync(ct);
                if (productId is int filterPid) rollups = rollups.Where(x => x.ProductId == filterPid).ToList();
                foreach (var r in rollups)
                    counts.Add(new OeeCountSlice(r.BucketTs, r.BucketTs + OeeMath.HourMs, r.GoodCount, r.RejectCount, r.ScrapCount, r.IdealCycleSec, r.TargetCount > 0 ? r.TargetCount : 0, r.ProductId));
                // 把预聚合时间合成伪区间（按 Run/Down/Fault/Setup/PlannedDown）。
                foreach (var r in rollups)
                {
                    long t = r.BucketTs;
                    void Add(long ms, OeeStateKind k) { if (ms > 0) { intervals.Add(new OeeInterval(t, t + ms, k)); t += ms; } }
                    // 顺序近似：计划停机 → 故障 → 换型 → 停机 → 运行（足以汇总时长）。
                    Add(r.PlannedDownMs, OeeStateKind.PlannedDown);
                    Add(r.FaultMs, OeeStateKind.Fault);
                    Add(r.SetupMs, OeeStateKind.Setup);
                    Add(r.DownMs - r.FaultMs - r.SetupMs, OeeStateKind.Stopped);
                    Add(r.RunMs, OeeStateKind.Running);
                }
            }

            string DayLabel(long ts)
            {
                var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ts), Zone);
                return local.ToString("yyyy-MM-dd");
            }
            // 按本地日切分 byDay
            var dayRows = new List<OeeBreakdownRow>();
            for (var d = DayStart(from); d < to; d = NextDay(d))
            {
                var de = Math.Min(NextDay(d), to);
                if (de <= Math.Max(d, from)) continue;
                var a = Math.Max(d, from);
                var row = OeeMath.ComputeBucket(a, de, DayLabel(d), intervals, counts, OeeMath.PlannedSegments(a, de, windows), th, line.Id, line.Name);
                dayRows.Add(row);
            }

            var kpi = OeeMath.Analyze(from, to, now, intervals, counts, windows, th, line.Id, line.Name);
            // 用时区日覆盖默认 UTC 日切
            kpi = kpi with { ByDay = dayRows };
            parts.Add(kpi);
        }

        var merged = Merge(parts);
        // 趋势：≤2 天按小时，否则按本地日。
        var trend = new List<OeeBreakdownRow>();
        if (to - from <= 2 * DayMs)
        {
            for (var h = FloorHour(from); h < to; h += OeeMath.HourMs)
            {
                var he = Math.Min(h + OeeMath.HourMs, to);
                var slice = OeeMath.ComputeBucket(Math.Max(h, from), he, DateTimeOffset.FromUnixTimeMilliseconds(h).ToString("MM-dd HH:mm"),
                    BuildIntervals(Math.Max(h, from), he, events, reasons),
                    corrections.Select(c => new OeeCountSlice(c.PeriodFrom, c.PeriodTo, c.GoodDelta, c.RejectDelta, c.ScrapDelta,
                        OeeMath.EffectiveIdealCycleSec(lines[0].RatedUnitsPerHour, lines[0].IdealCycleSec), lines[0].TargetUnitsPerHour ?? 0, c.ProductId)).ToList(),
                    OeeMath.PlannedSegments(Math.Max(h, from), he, windows), th);
                trend.Add(slice);
            }
        }
        else
        {
            trend.AddRange(merged.ByDay);
        }

        return new OeeReport(merged, detailFrom, usedRollups, trend, th);
    }

    private static OeeKpiResult Empty(long from, long to, long now) => new(
        from, to, now, (to - from) / (double)OeeMath.HourMs,
        0, 0, 0, 0, 0, 0, to - from, 0, 0, 0, 0,
        null, null, null, null, null, 0, 0, 0, null, null, 0, false, false,
        [], [], [], [], []);
}
