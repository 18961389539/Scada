using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Reports;

public sealed record ReportColumnHeader(string Key, string Title, string? Unit, int Decimals, ReportColumnKind Kind);
public sealed record ReportRow(string Label, long From, long To, double?[] Values);
public sealed record ReportTable(string Title, string PeriodKey, ReportPeriod Period, long From, long To, IReadOnlyList<ReportColumnHeader> Columns, IReadOnlyList<ReportRow> Rows, ReportRow Summary, long GeneratedTs);
public sealed class ReportTemplateException(string message) : Exception(message);

public static class ReportPeriods
{
    // 返回包含 at 的报表周期：标识、起止（UTC 毫秒）。时区取 ShiftTimeZone。
    public static (string Key, long From, long To) Range(ReportPeriod period, string shiftStarts, long at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at), zone).DateTime;
        DateTime start, end; string key;
        switch (period)
        {
            case ReportPeriod.Month:
                start = new DateTime(local.Year, local.Month, 1); end = start.AddMonths(1); key = start.ToString("yyyy-MM"); break;
            case ReportPeriod.Shift:
                var shifts = CalcJobMath.ParseShifts(shiftStarts);
                var tod = local.TimeOfDay;
                var index = shifts.ToList().FindLastIndex(x => x <= tod);
                start = index < 0 ? local.Date.AddDays(-1) + shifts[^1] : local.Date + shifts[index];
                var nextIndex = (index < 0 ? shifts.Count - 1 : index) + 1;
                end = nextIndex < shifts.Count ? start.Date + shifts[nextIndex] : start.Date.AddDays(1) + shifts[0];
                key = start.ToString("yyyy-MM-dd HH:mm"); break;
            default:
                start = local.Date; end = start.AddDays(1); key = start.ToString("yyyy-MM-dd"); break;
        }
        return (key, ToUtc(start, zone), ToUtc(end, zone));
    }

    public static (string Key, long From, long To) Previous(ReportPeriod period, string shiftStarts, long at, TimeZoneInfo zone)
        => Range(period, shiftStarts, Range(period, shiftStarts, at, zone).From - 1, zone);

    public static long ToUtc(DateTime local, TimeZoneInfo zone) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();

    // 行：班报/日报按小时，月报按天（均按本地时间边界）。
    public static IReadOnlyList<(string Label, long From, long To)> Rows(ReportPeriod period, long from, long to, TimeZoneInfo zone)
    {
        var rows = new List<(string, long, long)>();
        var cursor = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(from), zone).DateTime;
        while (true)
        {
            var start = ToUtc(cursor, zone); if (start >= to) break;
            var next = period == ReportPeriod.Month ? cursor.Date.AddDays(1) : cursor.AddHours(1);
            var end = Math.Min(ToUtc(next, zone), to);
            rows.Add((period == ReportPeriod.Month ? cursor.ToString("MM-dd") : cursor.ToString("HH:00"), start, end));
            cursor = next;
            if (rows.Count > 800) break;
        }
        return rows;
    }
}

public static class ReportTemplates
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_]{0,31}$");

    public static List<ReportColumn> Columns(string json)
    {
        try { return JsonSerializer.Deserialize<List<ReportColumn>>(string.IsNullOrWhiteSpace(json) ? "[]" : json, Json) ?? []; }
        catch (JsonException ex) { throw new ReportTemplateException("列定义 JSON 无效：" + ex.Message); }
    }

    // 结构校验（不查库）。返回公式编译结果，键为列键（小写）。
    public static Dictionary<string, TagExpression> Validate(ReportTemplate template, IReadOnlyList<ReportColumn> columns)
    {
        void Fail(string message) => throw new ReportTemplateException(message);
        if (template.Name.Trim().Length is 0 or > 100) Fail("名称须为 1～100 个字符");
        if (!Enum.IsDefined(template.Period)) Fail("报表周期无效");
        if (template.Period == ReportPeriod.Shift)
            try { CalcJobMath.ParseShifts(template.ShiftStarts); } catch (TagExpressionException ex) { Fail(ex.Message); }
        if (template.ScheduleDelayMin is < 0 or > 1440) Fail("生成延迟须为 0～1440 分钟");
        if (template.EmailChannelId is not null && NotificationRecipients(template.EmailRecipients).Count == 0) Fail("启用邮件投递时须填写收件人");
        if (columns.Count is 0 or > 40) Fail("列数须为 1～40");
        var seen = new HashSet<string>(); var formulas = new Dictionary<string, TagExpression>();
        foreach (var column in columns)
        {
            var label = string.IsNullOrWhiteSpace(column.Title) ? column.Key : column.Title;
            if (!KeyPattern.IsMatch(column.Key ?? "")) Fail($"列键须为字母开头的字母数字下划线（≤32）：{column.Key}");
            if (!seen.Add(column.Key!.ToLowerInvariant())) Fail($"列键重复：{column.Key}");
            if (string.IsNullOrWhiteSpace(column.Title) || column.Title.Length > 60) Fail($"列 {column.Key} 标题须为 1～60 个字符");
            if (column.Decimals is < 0 or > 6) Fail($"列 {label} 小数位须为 0～6");
            if (!double.IsFinite(column.Factor) || column.Factor == 0) Fail($"列 {label} 系数无效");
            switch (column.Kind)
            {
                case ReportColumnKind.TagAvg or ReportColumnKind.TagMax or ReportColumnKind.TagMin or ReportColumnKind.TagDiff or ReportColumnKind.TagSum:
                    if (column.TagId is not > 0) Fail($"列 {label} 须选择测点"); break;
                case ReportColumnKind.CalcSnapshot:
                    if (column.JobId is not > 0 || string.IsNullOrWhiteSpace(column.Output)) Fail($"列 {label} 须选择计算任务与输出"); break;
                case ReportColumnKind.AlarmCount:
                    if (column.MinLevel is < 0 or > 3) Fail($"列 {label} 最低等级无效"); break;
                case ReportColumnKind.AlarmKpi:
                    if (!AlarmKpiMetrics.Contains(column.Output ?? "")) Fail($"列 {label} 须选择报警 KPI 指标"); break;
                case ReportColumnKind.OeeKpi:
                    if (!OeeKpiMetrics.Contains(column.Output ?? "")) Fail($"列 {label} 须选择 OEE KPI 指标"); break;
                case ReportColumnKind.Formula:
                    TagExpression expression = null!;
                    try { expression = TagExpression.Compile(column.Formula ?? "", allowVariables: true); }
                    catch (TagExpressionException ex) { Fail($"列 {label} 公式错误：{ex.Message}"); }
                    if (expression.Dependencies.Count > 0) Fail($"列 {label} 公式只能用 $列键 引用本行其他列，不能直接引用测点");
                    foreach (var v in expression.Variables)
                        if (!seen.Contains(v) || v == column.Key.ToLowerInvariant()) Fail($"列 {label} 公式引用的 ${v} 须是排在它前面的列");
                    formulas[column.Key.ToLowerInvariant()] = expression; break;
                default: Fail($"列 {label} 类型无效"); break;
            }
        }
        return formulas;
    }

    // 报警 KPI 列可用的指标键。
    public static readonly IReadOnlyList<string> AlarmKpiMetrics =
    [
        "total", "annunciated", "perOperator10Min", "perOperatorHour", "peak10Min", "floodPercent", "floods",
        "avgAckSec", "medianAckSec", "avgClearSec", "medianClearSec", "chatterEpisodes", "fleeting", "standing", "stale",
        "shelvedHours", "suppressedHours", "highPercent"
    ];

    public static double? AlarmKpiValue(Scada.Engine.Alarms.AlarmAnalyticsResult k, string metric) => metric switch
    {
        "total" => k.Total, "annunciated" => k.Annunciated, "perOperator10Min" => k.PerOperator10Min, "perOperatorHour" => k.PerOperatorHour,
        "peak10Min" => k.Peak10Min, "floodPercent" => k.PercentTimeInFlood, "floods" => k.Floods.Count,
        "avgAckSec" => k.AvgAckSec, "medianAckSec" => k.MedianAckSec, "avgClearSec" => k.AvgClearSec, "medianClearSec" => k.MedianClearSec,
        "chatterEpisodes" => k.ChatteringSources.Sum(x => x.Episodes), "fleeting" => k.FleetingTotal, "standing" => k.Standing.Count, "stale" => k.Stale.Count,
        "shelvedHours" => k.ShelvedHours, "suppressedHours" => k.SuppressedHours,
        "highPercent" => k.Priority.FirstOrDefault(p => p.Band == "high")?.Percent,
        _ => null
    };

    public static readonly IReadOnlyList<string> OeeKpiMetrics =
    [
        "availability", "performance", "quality", "oee", "plannedHours", "runHours", "downHours",
        "good", "reject", "scrap", "total", "scrapRate", "mtbfHours", "mttrHours", "faultCount",
        "productionVsTarget", "target"
    ];

    public static double? OeeKpiValue(Scada.Engine.Oee.OeeKpiResult k, string metric) => metric switch
    {
        "availability" => k.Availability, "performance" => k.Performance, "quality" => k.Quality, "oee" => k.Oee,
        "plannedHours" => k.PlannedMs / (double)Scada.Engine.Oee.OeeMath.HourMs,
        "runHours" => k.RunMs / (double)Scada.Engine.Oee.OeeMath.HourMs,
        "downHours" => k.DownMs / (double)Scada.Engine.Oee.OeeMath.HourMs,
        "good" => k.Good, "reject" => k.Reject, "scrap" => k.Scrap, "total" => k.Total, "scrapRate" => k.ScrapRate,
        "mtbfHours" => k.MtbfHours, "mttrHours" => k.MttrHours, "faultCount" => k.FaultCount,
        "productionVsTarget" => k.ProductionVsTargetPercent, "target" => k.Target,
        _ => null
    };

    public static IReadOnlyList<string> NotificationRecipients(string? text) =>
        (text ?? "").Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

// 差值（表底/计数器）与累计（时间加权积分）的纯函数，便于单测。
public static class ReportMath
{
    public const long EdgeMs = 3_600_000; // 边界取值与桥接的最大间隔：1 小时

    // 边界值：t 前 1 小时内最后一个样本；没有则取 t 后 1 小时内第一个。
    public static double? ValueAt(IReadOnlyList<HistoryPoint> good, long t)
    {
        HistoryPoint? before = null, after = null;
        foreach (var p in good)
        {
            if (p.Ts <= t) { if (p.Ts >= t - EdgeMs) before = p; }
            else { if (p.Ts <= t + EdgeMs) after = p; break; }
        }
        return (before ?? after)?.Value;
    }

    // 计数器增量：从起点边界值开始逐点累加，读数回落视为表底复位（复位后从 0 起算，计入新读数）。
    public static double? CounterDelta(IReadOnlyList<HistoryPoint> good, long from, long to)
    {
        if (ValueAt(good, from) is not double previous || ValueAt(good, to) is not double end) return null;
        var total = 0.0;
        foreach (var p in good)
        {
            if (p.Ts <= from) continue;
            if (p.Ts > to) break;
            total += p.Value >= previous ? p.Value - previous : p.Value;
            previous = p.Value;
        }
        // 终点前 1 小时内无样本时终点值取自区间之后的样本，补上最后一段（与下一行起点取值一致，逐行之和等于整期）。
        if (!good.Any(p => p.Ts <= to && p.Ts >= to - EdgeMs)) total += end >= previous ? end - previous : end;
        return total;
    }

    // 梯形时间加权积分（单位：值 × 小时）。相邻样本间隔超过 1 小时不桥接；区间边界按线性插值截断。无覆盖返回 null。
    public static double? Integrate(IReadOnlyList<HistoryPoint> good, long from, long to)
    {
        double total = 0; var covered = false;
        for (var i = 1; i < good.Count; i++)
        {
            var a = good[i - 1]; var b = good[i];
            if (b.Ts <= from || a.Ts >= to || b.Ts - a.Ts > EdgeMs || b.Ts == a.Ts) continue;
            double At(long t) => a.Value + (b.Value - a.Value) * (t - a.Ts) / (b.Ts - a.Ts);
            var s = Math.Max(a.Ts, from); var e = Math.Min(b.Ts, to);
            total += (At(s) + At(e)) / 2 * (e - s) / 3_600_000.0; covered = true;
        }
        return covered ? total : null;
    }
}

// 报表计算：只读历史库、计算任务快照与报警表，结果为纯数据表；渲染（Excel/PDF）在服务端完成。
public sealed class ReportBuilder(ReportService history, IDbContextFactory<ConfigDbContext> factory, AccessModelProvider access, Scada.Engine.Alarms.AlarmAnalyticsService? alarmAnalytics = null, Scada.Engine.Oee.OeeService? oeeService = null)
{
    public async Task<ReportTable> BuildAsync(ReportTemplate template, string periodKey, long from, long to, TimeZoneInfo zone, CancellationToken ct)
    {
        var columns = ReportTemplates.Columns(template.ColumnsJson);
        var formulas = ReportTemplates.Validate(template, columns);
        var rows = ReportPeriods.Rows(template.Period, from, to, zone);
        var tagIds = columns.Where(c => c.TagId is int && c.Kind <= ReportColumnKind.TagSum).Select(c => c.TagId!.Value).Distinct().ToArray();
        var hourly = tagIds.Length == 0 ? [] : await history.HourlyAsync(tagIds, from, to, ct);

        // 差值与累计都基于原始样本（只取好质量、有限值），一次取整期 ±1 小时。
        var rawTags = columns.Where(c => c.Kind is ReportColumnKind.TagDiff or ReportColumnKind.TagSum).Select(c => c.TagId!.Value).Distinct().ToArray();
        var raw = new Dictionary<int, List<HistoryPoint>>();
        if (rawTags.Length > 0)
            foreach (var series in await history.RawAsync(rawTags, from - ReportMath.EdgeMs, to + ReportMath.EdgeMs, ct))
                raw[series.TagId] = series.Points.Where(p => p.Quality == TagQuality.Good && double.IsFinite(p.Value)).OrderBy(p => p.Ts).ToList();

        await using var db = await factory.CreateDbContextAsync(ct);
        var jobIds = columns.Where(c => c.Kind == ReportColumnKind.CalcSnapshot).Select(c => c.JobId!.Value).Distinct().ToArray();
        var snapshots = jobIds.Length == 0 ? [] : (await db.CalcJobSnapshots.AsNoTracking().Where(x => jobIds.Contains(x.JobId) && x.PeriodStartTs >= from && x.PeriodStartTs < to).ToListAsync(ct))
            .Select(x => (x.JobId, x.PeriodStartTs, Values: JsonSerializer.Deserialize<Dictionary<string, double>>(x.ValuesJson) ?? new())).ToList();
        List<(long StartTs, AlarmLevel Level, int? TagId, int? DeviceId)> alarms = [];
        if (columns.Any(c => c.Kind == ReportColumnKind.AlarmCount))
            alarms = (await db.AlarmEvents.AsNoTracking().Where(x => x.StartTs >= from && x.StartTs < to).Select(x => new { x.StartTs, x.Level, x.TagId, x.DeviceId }).ToListAsync(ct))
                .Select(x => (x.StartTs, x.Level, x.TagId, x.DeviceId)).ToList();
        var model = await access.GetAsync(ct);
        bool InArea(int? tagId, int? deviceId) =>
            tagId is int t ? model.AreasOfTag(t).Contains(template.AreaId)
            : deviceId is int d ? model.DeviceAreas.GetValueOrDefault(d, Area.DefaultId) == template.AreaId
            : template.AreaId == Area.DefaultId;

        // 报警 KPI：一次取整期样本（含开始前仍活动的），按行区间分别计算；同一区间只算一次。
        List<Scada.Engine.Alarms.AlarmSample> kpiSamples = []; List<Scada.Engine.Alarms.ShelveInterval> kpiShelves = [];
        Scada.Engine.Alarms.AlarmAnalyticsThresholds? kpiThresholds = null;
        var kpiCache = new Dictionary<(long, long), Scada.Engine.Alarms.AlarmAnalyticsResult>();
        var now = ScadaTime.NowMs();
        if (columns.Any(c => c.Kind == ReportColumnKind.AlarmKpi))
        {
            if (alarmAnalytics is null) throw new ReportTemplateException("报警 KPI 列不可用");
            bool Include(Scada.Engine.Alarms.AlarmSample s) => InArea(s.TagId, s.DeviceId);
            kpiThresholds = (await alarmAnalytics.GetThresholdsAsync(ct)).Thresholds;
            kpiSamples = (await alarmAnalytics.LoadSamplesAsync(from, to, Include, ct)).Samples;
            kpiShelves = await alarmAnalytics.LoadShelvesAsync(from, Math.Min(to, now), Include, ct);
        }
        double? Kpi(string metric, long rf, long rt)
        {
            if (!kpiCache.TryGetValue((rf, rt), out var k))
                kpiCache[(rf, rt)] = k = Scada.Engine.Alarms.AlarmAnalytics.Analyze(kpiSamples, kpiShelves, rf, rt, now, kpiThresholds!);
            return ReportTemplates.AlarmKpiValue(k, metric);
        }

        var oeeCache = new Dictionary<(long, long), Scada.Engine.Oee.OeeKpiResult>();
        List<int> oeeLineIds = [];
        Scada.Engine.Oee.OeeThresholds? oeeTh = null;
        if (columns.Any(c => c.Kind == ReportColumnKind.OeeKpi))
        {
            if (oeeService is null) throw new ReportTemplateException("OEE KPI 列不可用");
            oeeTh = (await oeeService.GetThresholdsAsync(ct)).Thresholds;
            oeeLineIds = await db.ProductionLines.AsNoTracking().Where(x => x.AreaId == template.AreaId && x.Enabled).Select(x => x.Id).ToListAsync(ct);
        }
        double? OeeKpi(string metric, long rf, long rt)
        {
            if (oeeLineIds.Count == 0 || !oeeCache.TryGetValue((rf, rt), out var k)) return null;
            return ReportTemplates.OeeKpiValue(k, metric);
        }

        double? Cell(ReportColumn c, long rf, long rt)
        {
            switch (c.Kind)
            {
                case ReportColumnKind.TagDiff:
                    return ReportMath.CounterDelta(raw.GetValueOrDefault(c.TagId!.Value) ?? [], rf, rt) * c.Factor;
                case ReportColumnKind.TagSum:
                    return ReportMath.Integrate(raw.GetValueOrDefault(c.TagId!.Value) ?? [], rf, rt) * c.Factor;
                case ReportColumnKind.CalcSnapshot:
                    var matched = snapshots.Where(s => s.JobId == c.JobId && s.PeriodStartTs >= rf && s.PeriodStartTs < rt && s.Values.ContainsKey(c.Output!)).ToList();
                    return matched.Count == 0 ? null : matched.Sum(s => s.Values[c.Output!]) * c.Factor;
                case ReportColumnKind.AlarmCount:
                    return alarms.Count(a => a.StartTs >= rf && a.StartTs < rt && (int)a.Level >= (c.MinLevel ?? 0) && InArea(a.TagId, a.DeviceId));
                case ReportColumnKind.AlarmKpi:
                    return Kpi(c.Output!, rf, rt) * c.Factor;
                case ReportColumnKind.OeeKpi:
                    return OeeKpi(c.Output!, rf, rt) * c.Factor;
                case ReportColumnKind.Formula: return null; // 稍后按行求值
            }
            var points = hourly.Where(p => p.TagId == c.TagId && p.Ts >= rf && p.Ts < rt && p.Count > 0).ToList();
            if (points.Count == 0) return null;
            return c.Kind switch
            {
                ReportColumnKind.TagAvg => points.Sum(p => p.Avg * p.Count) / points.Sum(p => p.Count) * c.Factor,
                ReportColumnKind.TagMax => points.Max(p => p.Max) * c.Factor,
                _ => points.Min(p => p.Min) * c.Factor
            };
        }

        void ApplyFormulas(double?[] values)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].Kind != ReportColumnKind.Formula) continue;
                var vars = new Dictionary<string, double?>();
                for (var j = 0; j < i; j++) vars[columns[j].Key.ToLowerInvariant()] = values[j];
                try
                {
                    values[i] = formulas[columns[i].Key.ToLowerInvariant()].Evaluate(new ExpressionContext(_ => throw new TagExpressionException("公式不能引用测点"),
                        name => vars.GetValueOrDefault(name) ?? throw new TagExpressionException("缺数据"), 10_000)) * columns[i].Factor;
                }
                catch (TagExpressionException) { values[i] = null; } // 缺数据或除零：该格留空
            }
        }

        if (oeeLineIds.Count > 0 && oeeService is not null)
        {
            var ranges = rows.Select(r => (r.From, r.To)).Append((rows.Count > 0 ? rows[0].From : from, rows.Count > 0 ? rows[^1].To : to)).Distinct().ToList();
            foreach (var (rf, rt) in ranges)
            {
                if (oeeCache.ContainsKey((rf, rt))) continue;
                var report = await oeeService.AnalyzeAsync(rf, rt, oeeLineIds, null, null, now, oeeTh!, ct);
                oeeCache[(rf, rt)] = report.Kpi;
            }
        }

        var result = new List<ReportRow>();
        foreach (var (label, rf, rt) in rows)
        {
            var values = columns.Select(c => Cell(c, rf, rt)).ToArray();
            ApplyFormulas(values); result.Add(new ReportRow(label, rf, rt, values));
        }
        // 汇总行：均值按样本加权（用整期重新计算），极值取整期，差值/累计/快照/报警数求和，公式按汇总值再算。
        var summary = columns.Select(c => c.Kind switch
        {
            ReportColumnKind.TagAvg or ReportColumnKind.TagMax or ReportColumnKind.TagMin or ReportColumnKind.TagDiff or ReportColumnKind.AlarmKpi or ReportColumnKind.OeeKpi => Cell(c, rows.Count > 0 ? rows[0].From : from, rows.Count > 0 ? rows[^1].To : to),
            ReportColumnKind.Formula => null,
            _ => result.Any(r => r.Values[columns.IndexOf(c)] is not null) ? result.Sum(r => r.Values[columns.IndexOf(c)] ?? 0) : (double?)null
        }).ToArray();
        ApplyFormulas(summary);
        var headers = columns.Select(c => new ReportColumnHeader(c.Key, c.Title, c.Unit, c.Decimals, c.Kind)).ToList();
        return new ReportTable(template.Name, periodKey, template.Period, from, to, headers, result, new ReportRow("合计/汇总", from, to, summary), ScadaTime.NowMs());
    }
}
