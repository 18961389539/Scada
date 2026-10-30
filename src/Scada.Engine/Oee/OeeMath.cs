namespace Scada.Engine.Oee;

// OEE 纯计算：输入时间切片（状态区间、产量、班次/计划），输出 A/P/Q/OEE、MTBF/MTTR、停机帕累托等。
// 不访问数据库，便于用构造序列单测；查询、区域过滤、预聚合由 OeeService / OeeRollupService 负责。

public enum OeeStateKind { Running, Stopped, Fault, Setup, PlannedDown, OutsideShift }

public sealed record OeeInterval(long StartTs, long EndTs, OeeStateKind Kind, int? ReasonId = null, string? ReasonCode = null, string? ReasonName = null, bool IsPlanned = false);
public sealed record OeeCountSlice(long StartTs, long EndTs, double Good, double Reject, double Scrap, double IdealCycleSec, double TargetPerHour, int? ProductId = null, string? ProductName = null);
public sealed record OeeShiftWindow(long StartTs, long EndTs, int ShiftId, string ShiftName);

public sealed record OeeDowntimeItem(int? ReasonId, string Code, string Name, string Category, bool IsPlanned, long Ms, double Hours, int Count, double Percent, double CumulativePercent);
public sealed record OeeFaultItem(long StartTs, long EndTs, long Ms, int? ReasonId, string? ReasonName);
public sealed record OeeBreakdownRow(
    long From, long To, string Label, int? LineId, string? LineName, int? ProductId, string? ProductName, int? ShiftId, string? ShiftName,
    long PlannedMs, long RunMs, long DownMs, long PlannedDownMs, long FaultMs, long SetupMs,
    double Good, double Reject, double Scrap, double Total,
    double? Availability, double? Performance, double? Quality, double? Oee,
    double? IdealCycleSec, double Target, double? MtbfHours, double? MttrHours, int FaultCount,
    bool PerformanceOver100);

public sealed record OeeKpiResult(
    long From, long To, long AsOf, double Hours,
    long PlannedMs, long RunMs, long DownMs, long PlannedDownMs, long FaultMs, long SetupMs, long OutsideMs,
    double Good, double Reject, double Scrap, double Total,
    double? Availability, double? Performance, double? Quality, double? Oee,
    double? IdealCycleSec, double Target, double ProductionVsTargetPercent,
    double ScrapRate,
    double? MtbfHours, double? MttrHours, int FaultCount,
    bool PerformanceOver100, bool PerformanceClamped,
    IReadOnlyList<OeeDowntimeItem> DowntimePareto,
    IReadOnlyList<OeeFaultItem> Faults,
    IReadOnlyList<OeeBreakdownRow> ByShift,
    IReadOnlyList<OeeBreakdownRow> ByProduct,
    IReadOnlyList<OeeBreakdownRow> ByDay);

public sealed class OeeThresholds
{
    // 性能 >100% 时：ClampPerformance=true 则封顶 1.0 并标记 PerformanceClamped；否则保留原值并标记 PerformanceOver100。
    public bool ClampPerformance { get; set; } = true;
    public double TargetAvailability { get; set; } = 0.90;
    public double TargetPerformance { get; set; } = 0.95;
    public double TargetQuality { get; set; } = 0.99;
    public double TargetOee { get; set; } = 0.85;
    public int TopN { get; set; } = 10;

    public OeeThresholds Clone() => (OeeThresholds)MemberwiseClone();

    public string? Validate()
    {
        if (TargetAvailability is < 0 or > 1 || TargetPerformance is < 0 or > 1 || TargetQuality is < 0 or > 1 || TargetOee is < 0 or > 1)
            return "OEE 目标须为 0～1";
        if (TopN is < 1 or > 100) return "Top N 须为 1～100";
        return null;
    }
}

public static class OeeMath
{
    public const long MinuteMs = 60_000L, HourMs = 3_600_000L, DayMs = 86_400_000L;

    public static double IdealCycleFromRate(double unitsPerHour) => unitsPerHour > 0 ? 3600.0 / unitsPerHour : 0;

    public static double EffectiveIdealCycleSec(double? ratedUnitsPerHour, double idealCycleSec) =>
        ratedUnitsPerHour is > 0 ? IdealCycleFromRate(ratedUnitsPerHour.Value) : Math.Max(0, idealCycleSec);

    // 半开区间 [from,to) 上按班次窗口计算计划生产时间；班次外记为 Outside。
    public static List<(long Start, long End, bool InShift, int? ShiftId, string? ShiftName)> PlannedSegments(
        long from, long to, IReadOnlyList<OeeShiftWindow> shifts)
    {
        var cuts = new SortedSet<long> { from, to };
        foreach (var s in shifts) { if (s.StartTs > from && s.StartTs < to) cuts.Add(s.StartTs); if (s.EndTs > from && s.EndTs < to) cuts.Add(s.EndTs); }
        var list = cuts.ToList();
        var result = new List<(long, long, bool, int?, string?)>();
        for (var i = 0; i < list.Count - 1; i++)
        {
            var a = list[i]; var b = list[i + 1];
            var hit = shifts.FirstOrDefault(s => s.StartTs <= a && s.EndTs > a);
            result.Add((a, b, hit is not null, hit?.ShiftId, hit?.ShiftName));
        }
        return result;
    }

    // 将状态区间裁剪到 [from,to)，并与班次叠加：班次外覆盖为 OutsideShift；计划停机 Kind=PlannedDown。
    public static List<OeeInterval> ClipIntervals(IReadOnlyList<OeeInterval> intervals, long from, long to)
    {
        var result = new List<OeeInterval>();
        foreach (var x in intervals.OrderBy(i => i.StartTs))
        {
            var a = Math.Max(x.StartTs, from); var b = Math.Min(x.EndTs, to);
            if (b > a) result.Add(x with { StartTs = a, EndTs = b });
        }
        return result;
    }

    public static OeeBreakdownRow ComputeBucket(
        long from, long to, string label,
        IReadOnlyList<OeeInterval> intervals, IReadOnlyList<OeeCountSlice> counts,
        IReadOnlyList<(long Start, long End, bool InShift, int? ShiftId, string? ShiftName)> planned,
        OeeThresholds th,
        int? lineId = null, string? lineName = null, int? productId = null, string? productName = null,
        int? shiftId = null, string? shiftName = null)
    {
        long plannedMs = 0, runMs = 0, downMs = 0, plannedDownMs = 0, faultMs = 0, setupMs = 0;
        var faults = new List<OeeFaultItem>();

        // 计划时间：班次内时长 − 计划停机（与班次重叠部分）。
        foreach (var p in planned.Where(p => p.InShift))
            plannedMs += Math.Max(0, Math.Min(p.End, to) - Math.Max(p.Start, from));

        foreach (var iv in ClipIntervals(intervals, from, to))
        {
            // 只统计落在班次内的时间（班次外不计入 A/P 的运行/停机）。
            foreach (var p in planned.Where(p => p.InShift))
            {
                var a = Math.Max(iv.StartTs, Math.Max(p.Start, from));
                var b = Math.Min(iv.EndTs, Math.Min(p.End, to));
                if (b <= a) continue;
                var ms = b - a;
                switch (iv.Kind)
                {
                    case OeeStateKind.Running: runMs += ms; break;
                    case OeeStateKind.PlannedDown: plannedDownMs += ms; break;
                    case OeeStateKind.Fault:
                        faultMs += ms; downMs += ms;
                        faults.Add(new OeeFaultItem(a, b, ms, iv.ReasonId, iv.ReasonName));
                        break;
                    case OeeStateKind.Setup: setupMs += ms; downMs += ms; break;
                    case OeeStateKind.Stopped: downMs += ms; break;
                }
            }
        }
        plannedMs = Math.Max(0, plannedMs - plannedDownMs);

        double good = 0, reject = 0, scrap = 0, idealWeighted = 0, idealWeight = 0, target = 0;
        foreach (var c in counts)
        {
            var a = Math.Max(c.StartTs, from); var b = Math.Min(c.EndTs, to);
            if (b <= a) continue;
            var frac = (b - a) / (double)Math.Max(1, c.EndTs - c.StartTs);
            // 计数按区间重叠比例分摊（分钟桶通常整段落入）；人工修正的 Period 也可能跨桶。
            good += c.Good * frac; reject += c.Reject * frac; scrap += c.Scrap * frac;
            if (c.IdealCycleSec > 0) { idealWeighted += c.IdealCycleSec * (b - a); idealWeight += b - a; }
            if (c.TargetPerHour > 0) target += c.TargetPerHour * ((b - a) / (double)HourMs);
        }
        var total = good + reject + scrap;
        var ideal = idealWeight > 0 ? idealWeighted / idealWeight : 0;

        var avail = plannedMs > 0 ? runMs / (double)plannedMs : (double?)null;
        double? perf = null; var over = false; var clamped = false;
        if (runMs > 0 && ideal > 0 && total > 0)
        {
            perf = (ideal * total) / (runMs / 1000.0);
            if (perf > 1)
            {
                over = true;
                if (th.ClampPerformance) { perf = 1; clamped = true; }
            }
        }
        else if (runMs > 0 && total == 0) perf = 0;
        var quality = total > 0 ? good / total : (double?)null;
        double? oee = avail is double a0 && perf is double p0 && quality is double q0 ? a0 * p0 * q0 : null;

        double? mtbf = null, mttr = null;
        if (faults.Count > 0)
        {
            var faultSum = faults.Sum(f => f.Ms);
            mttr = faultSum / (double)faults.Count / HourMs;
            // MTBF = 运行时间 / 故障次数（运行时间含故障之间的运行）。
            if (runMs > 0) mtbf = runMs / (double)faults.Count / HourMs;
        }

        var vsTarget = target > 0 ? total / target * 100 : 0;
        var scrapRate = total > 0 ? (reject + scrap) / total : 0;

        return new OeeBreakdownRow(from, to, label, lineId, lineName, productId, productName, shiftId, shiftName,
            plannedMs, runMs, downMs, plannedDownMs, faultMs, setupMs,
            good, reject, scrap, total, avail, perf, quality, oee, ideal > 0 ? ideal : null, target,
            mtbf, mttr, faults.Count, over || clamped);
    }

    public static OeeKpiResult Analyze(
        long from, long to, long asOf,
        IReadOnlyList<OeeInterval> intervals,
        IReadOnlyList<OeeCountSlice> counts,
        IReadOnlyList<OeeShiftWindow> shifts,
        OeeThresholds? thresholds = null,
        int? lineId = null, string? lineName = null,
        Func<long, string>? dayLabel = null)
    {
        var th = thresholds ?? new OeeThresholds();
        var planned = PlannedSegments(from, to, shifts);
        var overall = ComputeBucket(from, to, "total", intervals, counts, planned, th, lineId, lineName);

        // 停机帕累托：按原因汇总班次内停机（不含计划停机的计划部分已单独）。
        var downGroups = new Dictionary<string, (int? Id, string Code, string Name, string Cat, bool Planned, long Ms, int Count)>();
        foreach (var iv in ClipIntervals(intervals, from, to))
        {
            if (iv.Kind is OeeStateKind.Running or OeeStateKind.OutsideShift) continue;
            foreach (var p in planned.Where(p => p.InShift))
            {
                var a = Math.Max(iv.StartTs, Math.Max(p.Start, from));
                var b = Math.Min(iv.EndTs, Math.Min(p.End, to));
                if (b <= a) continue;
                var key = iv.ReasonId?.ToString() ?? iv.ReasonCode ?? iv.Kind.ToString();
                var cat = iv.Kind == OeeStateKind.PlannedDown || iv.IsPlanned ? "availability"
                    : iv.Kind == OeeStateKind.Setup ? "performance" : "availability";
                if (iv.Kind == OeeStateKind.PlannedDown) cat = "availability";
                if (!downGroups.TryGetValue(key, out var g))
                    g = (iv.ReasonId, iv.ReasonCode ?? key, iv.ReasonName ?? iv.Kind.ToString(), cat, iv.IsPlanned || iv.Kind == OeeStateKind.PlannedDown, 0, 0);
                g.Ms += b - a; g.Count++;
                downGroups[key] = g;
            }
        }
        var downTotal = downGroups.Values.Sum(x => x.Ms);
        var pareto = downGroups.Values.OrderByDescending(x => x.Ms).Take(th.TopN).Select((x, i) =>
        {
            var pct = downTotal > 0 ? x.Ms * 100.0 / downTotal : 0;
            var cum = downGroups.Values.OrderByDescending(y => y.Ms).Take(i + 1).Sum(y => y.Ms) * 100.0 / Math.Max(1, downTotal);
            return new OeeDowntimeItem(x.Id, x.Code, x.Name, x.Cat, x.Planned, x.Ms, x.Ms / (double)HourMs, x.Count, Math.Round(pct, 2), Math.Round(cum, 2));
        }).ToList();

        var faults = new List<OeeFaultItem>();
        foreach (var iv in ClipIntervals(intervals, from, to).Where(i => i.Kind == OeeStateKind.Fault))
            foreach (var p in planned.Where(p => p.InShift))
            {
                var a = Math.Max(iv.StartTs, Math.Max(p.Start, from));
                var b = Math.Min(iv.EndTs, Math.Min(p.End, to));
                if (b > a) faults.Add(new OeeFaultItem(a, b, b - a, iv.ReasonId, iv.ReasonName));
            }

        // 按班次分解
        var byShift = shifts
            .Select(s => ComputeBucket(Math.Max(s.StartTs, from), Math.Min(s.EndTs, to), s.ShiftName, intervals, counts, planned, th, lineId, lineName, shiftId: s.ShiftId, shiftName: s.ShiftName))
            .Where(r => r.To > r.From)
            .ToList();

        // 按产品分解（有产量切片的产品）
        var productIds = counts.Where(c => c.ProductId is not null).Select(c => c.ProductId!.Value).Distinct().ToList();
        var byProduct = productIds.Select(pid =>
        {
            var name = counts.FirstOrDefault(c => c.ProductId == pid)?.ProductName ?? $"#{pid}";
            var pc = counts.Where(c => c.ProductId == pid).ToList();
            return ComputeBucket(from, to, name, intervals, pc, planned, th, lineId, lineName, productId: pid, productName: name);
        }).ToList();

        // 按本地日分解
        var byDay = new List<OeeBreakdownRow>();
        if (dayLabel is not null)
        {
            var cursor = from;
            while (cursor < to)
            {
                // 仅按 from 起每 24h 粗切，不做时区对齐；此分支目前无调用方（OeeService 传 null 后用自己的
                // 时区日行覆盖 ByDay），保留给只提供 dayLabel 的旧调用方式。
                var next = Math.Min(to, cursor + DayMs);
                byDay.Add(ComputeBucket(cursor, next, dayLabel(cursor), intervals, counts, planned, th, lineId, lineName));
                cursor = next;
            }
        }

        long outsideMs = planned.Where(p => !p.InShift).Sum(p => Math.Max(0, Math.Min(p.End, to) - Math.Max(p.Start, from)));
        var perfOver = overall.PerformanceOver100;
        var clamped = th.ClampPerformance && perfOver;

        return new OeeKpiResult(from, to, asOf, (to - from) / (double)HourMs,
            overall.PlannedMs, overall.RunMs, overall.DownMs, overall.PlannedDownMs, overall.FaultMs, overall.SetupMs, outsideMs,
            overall.Good, overall.Reject, overall.Scrap, overall.Total,
            overall.Availability, overall.Performance, overall.Quality, overall.Oee,
            overall.IdealCycleSec, overall.Target,
            overall.Target > 0 ? overall.Total / overall.Target * 100 : 0,
            overall.Total > 0 ? (overall.Reject + overall.Scrap) / overall.Total : 0,
            overall.MtbfHours, overall.MttrHours, overall.FaultCount,
            perfOver, clamped, pareto, faults, byShift, byProduct, byDay);
    }

    // 从分钟/小时预聚合行直接汇总 KPI（长区间快速路径）。
    public static OeeBreakdownRow FromRollups(
        long from, long to, string label, IEnumerable<(long PlannedMs, long RunMs, long DownMs, long PlannedDownMs, long FaultMs, long SetupMs, double Good, double Reject, double Scrap, double IdealCycleSec, double TargetCount)> rows,
        OeeThresholds th, int? lineId = null, string? lineName = null, int? productId = null, string? productName = null)
    {
        long planned = 0, run = 0, down = 0, pdown = 0, fault = 0, setup = 0;
        double good = 0, reject = 0, scrap = 0, idealW = 0, idealT = 0, target = 0;
        foreach (var r in rows)
        {
            planned += r.PlannedMs; run += r.RunMs; down += r.DownMs; pdown += r.PlannedDownMs; fault += r.FaultMs; setup += r.SetupMs;
            good += r.Good; reject += r.Reject; scrap += r.Scrap; target += r.TargetCount;
            if (r.IdealCycleSec > 0 && r.RunMs > 0) { idealW += r.IdealCycleSec * r.RunMs; idealT += r.RunMs; }
        }
        var total = good + reject + scrap;
        var ideal = idealT > 0 ? idealW / idealT : 0;
        var avail = planned > 0 ? run / (double)planned : (double?)null;
        double? perf = null; var over = false;
        if (run > 0 && ideal > 0 && total > 0)
        {
            perf = (ideal * total) / (run / 1000.0);
            if (perf > 1) { over = true; if (th.ClampPerformance) perf = 1; }
        }
        else if (run > 0 && total == 0) perf = 0;
        var quality = total > 0 ? good / total : (double?)null;
        double? oee = avail is double a && perf is double p && quality is double q ? a * p * q : null;
        double? mtbf = fault > 0 && run > 0 ? run / (double)HourMs / Math.Max(1, fault > 0 ? 1 : 1) : null; // 粗略：预聚合无故障次数，留给明细路径
        return new OeeBreakdownRow(from, to, label, lineId, lineName, productId, productName, null, null,
            planned, run, down, pdown, fault, setup, good, reject, scrap, total, avail, perf, quality, oee,
            ideal > 0 ? ideal : null, target, null, fault > 0 ? fault / (double)HourMs : null, 0, over);
    }
}
