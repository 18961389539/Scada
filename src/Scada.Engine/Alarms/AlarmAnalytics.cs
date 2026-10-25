using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Engine.Alarms;

// 报警统计分析（ISA-18.2 / EEMUA 191）的纯计算部分：输入报警样本与暂挂区间，输出 KPI、坏分子、洪水、建议。
// 不访问数据库，便于用构造的事件序列做单元测试；查询、区域过滤、预聚合由 AlarmAnalyticsService 负责。

public sealed record AlarmSample(
    int Id, int? RuleId, int? TagId, int? DeviceId, int? GatewayId, AlarmLevel Level,
    long StartTs, long? EndTs, long? AckTs, string? AckUser,
    bool Shelved, bool Suppressed, bool Chattering, string Message)
{
    // 报警源键：同一规则（或无规则时同一测点/设备/网关/消息）视为同一条报警，用于坏分子统计与预聚合。
    public string Source => SourceOf(RuleId, TagId, DeviceId, GatewayId, Message);
    public bool Annunciated => !Shelved && !Suppressed && !Chattering;

    public static string SourceOf(int? ruleId, int? tagId, int? deviceId, int? gatewayId, string message) =>
        ruleId is int r ? $"r{r}" : tagId is int t ? $"t{t}" : deviceId is int d ? $"d{d}" : gatewayId is int g ? $"g{g}"
        : "m:" + (message.Length > 100 ? message[..100] : message);

    public static AlarmSample From(AlarmEvent e) => new(e.Id, e.RuleId, e.TagId, e.DeviceId, e.GatewayId, e.Level, e.StartTs, e.EndTs, e.AckTs, e.AckUser, e.Shelved, e.Suppressed, e.Chattering, e.Message);
}

public sealed record ShelveInterval(int RuleId, long StartTs, long EndTs);

// 可配置阈值。默认值参照 EEMUA 191 与 ISA-18.2 推荐。
public sealed class AlarmAnalyticsThresholds
{
    // 统计每操作员报警率时的操作员岗位数；0 表示按统计期内确认过报警的人数（至少 1）。
    public int OperatorPositions { get; set; } = 1;
    public double AcceptablePer10Min { get; set; } = 1;
    public double ManageablePer10Min { get; set; } = 2;
    // 洪水：10 分钟内每操作员报警数 > 起始阈值进入洪水，降到 < 结束阈值退出（滞回）。
    public int FloodStartPer10Min { get; set; } = 10;
    public int FloodEndPer10Min { get; set; } = 5;
    public int ChatterCount { get; set; } = 3;
    public int ChatterWindowSec { get; set; } = 60;
    // 瞬时报警：持续不超过该秒数即恢复、且恢复前没人确认。
    public int FleetingSec { get; set; } = 10;
    public double StandingHours { get; set; } = 24;
    // 陈旧报警：仍活动且持续未确认超过该小时数。
    public double StaleHours { get; set; } = 24;
    // 关联/重复报警：两条报警在该秒数内相继出现视为一次同现。
    public int ConsequentialWindowSec { get; set; } = 5;
    public int ConsequentialMinCount { get; set; } = 5;
    public double ConsequentialMinRatio { get; set; } = 0.8;
    // 频繁报警建议阈值：平均每天超过该次数。
    public double FrequentPerDay { get; set; } = 10;
    public double TargetLowPercent { get; set; } = 80;
    public double TargetMediumPercent { get; set; } = 15;
    public double TargetHighPercent { get; set; } = 5;
    public int TopN { get; set; } = 10;

    public AlarmAnalyticsThresholds Clone() => (AlarmAnalyticsThresholds)MemberwiseClone();

    // 返回错误消息（中文源文案，由本地化层翻译）；null 表示有效。
    public string? Validate()
    {
        if (OperatorPositions is < 0 or > 100) return "操作员岗位数须为 0～100";
        if (!(AcceptablePer10Min > 0) || !(ManageablePer10Min >= AcceptablePer10Min) || ManageablePer10Min > 1000) return "可接受/可管理报警率无效";
        if (FloodStartPer10Min is < 1 or > 10000 || FloodEndPer10Min < 1 || FloodEndPer10Min > FloodStartPer10Min) return "洪水起止阈值无效（结束阈值须 ≤ 起始阈值）";
        if (ChatterCount is < 2 or > 1000 || ChatterWindowSec is < 1 or > 86400) return "抖动判定参数无效";
        if (FleetingSec is < 1 or > 3600) return "瞬时报警秒数须为 1～3600";
        if (StandingHours is < 0.1 or > 8760 || StaleHours is < 0.1 or > 8760) return "长期驻留/陈旧小时数须为 0.1～8760";
        if (ConsequentialWindowSec is < 1 or > 3600 || ConsequentialMinCount is < 2 or > 100000 || ConsequentialMinRatio is <= 0 or > 1) return "关联报警参数无效";
        if (!(FrequentPerDay > 0) || FrequentPerDay > 100000) return "频繁报警阈值无效";
        if (TargetLowPercent < 0 || TargetMediumPercent < 0 || TargetHighPercent < 0 || Math.Abs(TargetLowPercent + TargetMediumPercent + TargetHighPercent - 100) > 0.01) return "优先级目标比例须合计 100%";
        if (TopN is < 1 or > 100) return "Top N 须为 1～100";
        return null;
    }
}

public sealed record AlarmRef(int Id, string Source, int? RuleId, int? TagId, int? DeviceId, string Message, string Level, long StartTs);
public sealed record AlarmSourceCount(string Source, int? RuleId, int? TagId, int? DeviceId, string Message, string Level, int Count);
public sealed record AlarmBadActor(string Source, int? RuleId, int? TagId, int? DeviceId, string Message, string Level,
    int Count, int Annunciated, int ChatterEpisodes, int Fleeting, double Percent, double CumulativePercent);
public sealed record AlarmChatterItem(string Source, int? RuleId, string Message, string Level, int Activations, int Episodes, int MaxInWindow);
public sealed record AlarmFleetingItem(string Source, int? RuleId, string Message, string Level, int Count, int Total, double Ratio, double P90DurationSec);
public sealed record AlarmConsequentialPair(string SourceA, string MessageA, int? RuleA, int CountA, string SourceB, string MessageB, int? RuleB, int CountB, int Together, double Ratio);
public sealed record AlarmFloodPeriod(long StartTs, long EndTs, int Count, int Peak10Min, AlarmRef? FirstOut, IReadOnlyList<AlarmSourceCount> Contributors, IReadOnlyList<int> EventIds);
public sealed record AlarmPriorityItem(string Level, string Band, int Count, double Percent, double TargetPercent);
public sealed record AlarmActiveItem(int Id, string Source, int? RuleId, int? TagId, string Message, string Level, long StartTs, double Hours, bool Acked);
public sealed record AlarmShelveStat(int RuleId, int Count, double Hours);
public sealed record AlarmRateBucket(long StartTs, int Count);

public sealed record AlarmAnalyticsResult(
    long From, long To, long AsOf, double Hours,
    int Total, int Annunciated, int Shelved, int Suppressed, int Chattering,
    int OperatorPositions,
    double PerOperator10Min, double PerOperatorHour, double PerOperatorDay,
    int Peak10Min, long? Peak10MinTs, string Rating,
    // 10 分钟桶分布（每操作员）：可接受/可管理/偏高/洪水 的桶占比（%）
    double PctBucketsAcceptable, double PctBucketsManageable, double PctBucketsDemanding, double PctBucketsFlood,
    double PercentTimeInFlood, IReadOnlyList<AlarmFloodPeriod> Floods,
    int AckCount, double? AvgAckSec, double? MedianAckSec, int ClearCount, double? AvgClearSec, double? MedianClearSec,
    IReadOnlyList<AlarmActiveItem> Standing, IReadOnlyList<AlarmActiveItem> Stale, int StandingDuringWindow,
    int ShelveCount, double ShelvedHours, IReadOnlyList<AlarmShelveStat> ShelvedByRule, double SuppressedHours,
    IReadOnlyList<AlarmPriorityItem> Priority,
    IReadOnlyList<AlarmBadActor> Top,
    IReadOnlyList<AlarmChatterItem> ChatteringSources,
    IReadOnlyList<AlarmFleetingItem> Fleeting, int FleetingTotal,
    IReadOnlyList<AlarmConsequentialPair> Consequential);

public sealed record AlarmRuleConfig(int RuleId, string Name, AlarmLevel Level, int DelaySec, int OffDelaySec, double RecoverDeadband);
public sealed record AlarmSuggestion(string Kind, string Source, int? RuleId, string Message, string Level, int Severity,
    IReadOnlyDictionary<string, double> Evidence, IReadOnlyDictionary<string, double> Current, IReadOnlyDictionary<string, double> Suggested);

public static class AlarmAnalytics
{
    public const long Bucket10 = 10 * 60_000L;
    public const long HourMs = 3_600_000L;

    public static long Floor10(long ts) => (long)Math.Floor(ts / (double)Bucket10) * Bucket10;

    public static string Band(AlarmLevel level) => level switch { AlarmLevel.Critical => "high", AlarmLevel.Warning => "medium", _ => "low" };

    // 按 EEMUA 191 表述给出平均报警率评级（每操作员每 10 分钟）。
    public static string RateRating(double per10Min, AlarmAnalyticsThresholds th) =>
        per10Min <= th.AcceptablePer10Min ? "acceptable"
        : per10Min <= th.ManageablePer10Min ? "manageable"
        : per10Min <= th.FloodEndPer10Min ? "possiblyOverDemanding"
        : per10Min <= th.FloodStartPer10Min ? "likelyOverDemanding"
        : "unacceptable";

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        var rank = (int)Math.Ceiling(p * values.Count) - 1;
        return values[Math.Clamp(rank, 0, values.Count - 1)];
    }

    private static double R(double v, int digits = 2) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
    private static double? R(double? v, int digits = 1) => v is double x ? Math.Round(x, digits, MidpointRounding.AwayFromZero) : null;

    // 10 分钟报警率（只统计需报警的、开始时刻落在 [from,to) 内的报警），桶与纪元对齐，覆盖窗口内所有桶（含 0）。
    public static List<AlarmRateBucket> Buckets10(IEnumerable<AlarmSample> samples, long from, long to)
    {
        var counts = new Dictionary<long, int>();
        foreach (var s in samples)
            if (s.Annunciated && s.StartTs >= from && s.StartTs < to) counts[Floor10(s.StartTs)] = counts.GetValueOrDefault(Floor10(s.StartTs)) + 1;
        var list = new List<AlarmRateBucket>();
        for (var b = Floor10(from); b < to; b += Bucket10) list.Add(new(b, counts.GetValueOrDefault(b)));
        return list;
    }

    // 洪水检测：桶内每操作员报警数 > 起始阈值进入洪水；之后只要 ≥ 结束阈值就延续；低于结束阈值时退出。
    public static List<(long Start, long End, int Peak)> DetectFloods(IReadOnlyList<AlarmRateBucket> buckets, int positions, AlarmAnalyticsThresholds th)
    {
        var floods = new List<(long, long, int)>();
        long? start = null; var peak = 0;
        foreach (var b in buckets)
        {
            var rate = b.Count / (double)positions;
            if (start is null)
            {
                if (rate > th.FloodStartPer10Min) { start = b.StartTs; peak = b.Count; }
            }
            else if (rate >= th.FloodEndPer10Min) peak = Math.Max(peak, b.Count);
            else { floods.Add((start.Value, b.StartTs, peak)); start = null; }
        }
        if (start is not null) floods.Add((start.Value, buckets[^1].StartTs + Bucket10, peak));
        return floods;
    }

    // 抖动：同一报警源在 window 内触发 ≥ count 次记为一次抖动片段（片段不重叠）。返回片段数与窗口内最大触发次数。
    public static (int Episodes, int MaxInWindow) ChatterEpisodes(IReadOnlyList<long> sortedStarts, int count, long windowMs)
    {
        int episodes = 0, max = sortedStarts.Count > 0 ? 1 : 0, i = 0;
        while (i < sortedStarts.Count)
        {
            var j = i;
            while (j < sortedStarts.Count && sortedStarts[j] - sortedStarts[i] <= windowMs) j++;
            var n = j - i; max = Math.Max(max, n);
            if (n >= count) { episodes++; i = j; } else i++;
        }
        return (episodes, max);
    }

    public static bool IsFleeting(AlarmSample s, AlarmAnalyticsThresholds th) =>
        s.EndTs is long end && end - s.StartTs <= th.FleetingSec * 1000L && (s.AckTs is not long ack || ack >= end);

    private static long Overlap(long a0, long a1, long b0, long b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    // samples 应包含：开始于 [from,to) 的报警，以及开始更早但在 from 时仍活动的报警（用于驻留/抑制时长）。
    public static AlarmAnalyticsResult Analyze(IReadOnlyList<AlarmSample> samples, IReadOnlyList<ShelveInterval> shelves, long from, long to, long now, AlarmAnalyticsThresholds th)
    {
        if (to <= from) to = from + 1;
        var asOf = Math.Min(to, now);
        var hours = (to - from) / (double)HourMs;
        var inWindow = samples.Where(s => s.StartTs >= from && s.StartTs < to).OrderBy(s => s.StartTs).ThenBy(s => s.Id).ToList();
        var ann = inWindow.Where(s => s.Annunciated).ToList();

        var ackers = inWindow.Where(s => s.AckTs is long a && a >= from && a < to && !string.IsNullOrEmpty(s.AckUser)).Select(s => s.AckUser!).Distinct().Count();
        var positions = th.OperatorPositions > 0 ? th.OperatorPositions : Math.Max(1, ackers);

        var buckets = Buckets10(inWindow, from, to);
        var peakBucket = buckets.Count == 0 ? null : buckets.MaxBy(b => b.Count);
        var floods = DetectFloods(buckets, positions, th);
        var floodMs = floods.Sum(f => Overlap(f.Start, f.End, from, to));
        int acc = 0, man = 0, dem = 0, fl = 0;
        foreach (var b in buckets)
        {
            var rate = b.Count / (double)positions;
            if (rate <= th.AcceptablePer10Min) acc++; else if (rate <= th.ManageablePer10Min) man++; else if (rate <= th.FloodStartPer10Min) dem++; else fl++;
        }
        var nb = Math.Max(1, buckets.Count);
        var windows10 = (to - from) / (double)Bucket10;
        var per10 = ann.Count / windows10 / positions;

        var floodPeriods = floods.Select(f =>
        {
            var members = ann.Where(s => s.StartTs >= f.Start && s.StartTs < f.End).ToList();
            var first = members.FirstOrDefault();
            return new AlarmFloodPeriod(f.Start, f.End, members.Count, f.Peak,
                first is null ? null : Ref(first),
                members.GroupBy(s => s.Source).Select(g => SourceCount(g.Last(), g.Count())).OrderByDescending(x => x.Count).ThenBy(x => x.Message, StringComparer.Ordinal).Take(10).ToList(),
                members.Select(s => s.Id).Take(500).ToList());
        }).ToList();

        var ackTimes = ann.Where(s => s.AckTs is long a && a >= s.StartTs).Select(s => (s.AckTs!.Value - s.StartTs) / 1000d).ToList();
        var clearTimes = ann.Where(s => s.EndTs is long e && e >= s.StartTs).Select(s => (s.EndTs!.Value - s.StartTs) / 1000d).ToList();

        // 长期驻留/陈旧：以 asOf（窗口结束或当前时刻，取较早者）为观察点。
        var standingMs = (long)(th.StandingHours * HourMs); var staleMs = (long)(th.StaleHours * HourMs);
        bool ActiveAt(AlarmSample s, long t) => s.StartTs <= t && (s.EndTs is not long e || e > t);
        AlarmActiveItem Active(AlarmSample s) => new(s.Id, s.Source, s.RuleId, s.TagId, s.Message, s.Level.ToString(), s.StartTs, R((asOf - s.StartTs) / (double)HourMs), s.AckTs is long a && a <= asOf);
        var standing = samples.Where(s => ActiveAt(s, asOf) && asOf - s.StartTs >= standingMs).OrderBy(s => s.StartTs).Select(Active).ToList();
        var stale = samples.Where(s => ActiveAt(s, asOf) && asOf - s.StartTs >= staleMs && (s.AckTs is not long a || a > asOf)).OrderBy(s => s.StartTs).Select(Active).ToList();
        // 窗口内任意时刻曾达到长期驻留的报警数（含窗口开始前已活动的）。
        var standingDuring = samples.Count(s => { var end = Math.Min(s.EndTs ?? asOf, asOf); return end >= from && end - s.StartTs >= standingMs; });

        var clipped = shelves.Select(x => (x.RuleId, Ms: Overlap(x.StartTs, Math.Min(x.EndTs, asOf), from, asOf))).ToList();
        var shelveCount = shelves.Count(x => x.StartTs < asOf && x.EndTs > from);
        var shelvedByRule = shelves.Where(x => x.StartTs < asOf && x.EndTs > from).GroupBy(x => x.RuleId)
            .Select(g => new AlarmShelveStat(g.Key, g.Count(), R(g.Sum(x => Overlap(x.StartTs, Math.Min(x.EndTs, asOf), from, asOf)) / (double)HourMs)))
            .OrderByDescending(x => x.Hours).ThenBy(x => x.RuleId).ToList();
        var suppressedMs = samples.Where(s => s.Suppressed).Sum(s => Overlap(s.StartTs, s.EndTs ?? asOf, from, asOf));

        var total = Math.Max(1, ann.Count);
        double Target(string band) => band switch { "high" => th.TargetHighPercent, "medium" => th.TargetMediumPercent, _ => th.TargetLowPercent };
        var priority = new[] { AlarmLevel.Info, AlarmLevel.Warning, AlarmLevel.Critical }
            .Select(l => { var c = ann.Count(s => s.Level == l); return new AlarmPriorityItem(l.ToString(), Band(l), c, R(ann.Count == 0 ? 0 : c * 100d / total), Target(Band(l))); }).ToList();

        // 坏分子：按报警源统计所有报警（含暂挂/抑制/抖动标记），帕累托累计占比。
        var chatterWindow = th.ChatterWindowSec * 1000L;
        var groups = inWindow.GroupBy(s => s.Source).Select(g =>
        {
            var list = g.ToList();
            var (episodes, max) = ChatterEpisodes(list.Select(s => s.StartTs).ToList(), th.ChatterCount, chatterWindow);
            return (Last: list[^1], List: list, Episodes: episodes, Max: max, Fleeting: list.Count(s => IsFleeting(s, th)));
        }).ToList();
        var allCount = Math.Max(1, inWindow.Count);
        double cumulative = 0;
        var top = groups.OrderByDescending(g => g.List.Count).ThenBy(g => g.Last.Message, StringComparer.Ordinal).Take(th.TopN).Select(g =>
        {
            var pct = g.List.Count * 100d / allCount; cumulative += pct;
            return new AlarmBadActor(g.Last.Source, g.Last.RuleId, g.Last.TagId, g.Last.DeviceId, g.Last.Message, g.Last.Level.ToString(),
                g.List.Count, g.List.Count(s => s.Annunciated), g.Episodes, g.Fleeting, R(pct), R(cumulative));
        }).ToList();
        var chatter = groups.Where(g => g.Episodes > 0).OrderByDescending(g => g.Episodes).ThenByDescending(g => g.List.Count).ThenBy(g => g.Last.Source, StringComparer.Ordinal).Take(50)
            .Select(g => new AlarmChatterItem(g.Last.Source, g.Last.RuleId, g.Last.Message, g.Last.Level.ToString(), g.List.Count, g.Episodes, g.Max)).ToList();
        var fleeting = groups.Where(g => g.Fleeting > 0).OrderByDescending(g => g.Fleeting).ThenBy(g => g.Last.Source, StringComparer.Ordinal).Take(50)
            .Select(g => new AlarmFleetingItem(g.Last.Source, g.Last.RuleId, g.Last.Message, g.Last.Level.ToString(), g.Fleeting, g.List.Count, R(g.Fleeting / (double)g.List.Count, 3),
                R(Percentile(g.List.Where(s => IsFleeting(s, th)).Select(s => (s.EndTs!.Value - s.StartTs) / 1000d).ToList(), 0.9), 1))).ToList();

        return new AlarmAnalyticsResult(
            from, to, asOf, R(hours, 3),
            inWindow.Count, ann.Count, inWindow.Count(s => s.Shelved), inWindow.Count(s => s.Suppressed), inWindow.Count(s => s.Chattering),
            positions,
            R(per10, 3), R(ann.Count / hours / positions), R(ann.Count / hours * 24 / positions),
            peakBucket?.Count ?? 0, peakBucket is { Count: > 0 } ? peakBucket.StartTs : null, RateRating(per10, th),
            R(acc * 100d / nb), R(man * 100d / nb), R(dem * 100d / nb), R(fl * 100d / nb),
            R(floodMs * 100d / (to - from)), floodPeriods,
            ackTimes.Count, R(ackTimes.Count == 0 ? null : ackTimes.Average()), R(Median(ackTimes)),
            clearTimes.Count, R(clearTimes.Count == 0 ? null : clearTimes.Average()), R(Median(clearTimes)),
            standing, stale, standingDuring,
            shelveCount, R(clipped.Sum(x => x.Ms) / (double)HourMs), shelvedByRule, R(suppressedMs / (double)HourMs),
            priority, top, chatter, fleeting, inWindow.Count(s => IsFleeting(s, th)),
            Consequential(inWindow, th));
    }

    public static AlarmRef Ref(AlarmSample s) => new(s.Id, s.Source, s.RuleId, s.TagId, s.DeviceId, s.Message, s.Level.ToString(), s.StartTs);
    private static AlarmSourceCount SourceCount(AlarmSample s, int count) => new(s.Source, s.RuleId, s.TagId, s.DeviceId, s.Message, s.Level.ToString(), count);

    // 关联/重复报警：对每条报警，看其后 window 内出现的其他报警源（每个源只计一次），累加到无序对。
    // 比例 = 同现次数 / 两者中较少的报警次数（封顶 1）。达到最少次数与比例的对才列出。
    public static List<AlarmConsequentialPair> Consequential(IReadOnlyList<AlarmSample> sorted, AlarmAnalyticsThresholds th)
    {
        var window = th.ConsequentialWindowSec * 1000L;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var last = new Dictionary<string, AlarmSample>(StringComparer.Ordinal);
        foreach (var s in sorted) { counts[s.Source] = counts.GetValueOrDefault(s.Source) + 1; last[s.Source] = s; }
        var pairs = new Dictionary<(string, string), int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sorted.Count; i++)
        {
            seen.Clear();
            var a = sorted[i].Source;
            for (var j = i + 1; j < sorted.Count && j - i <= 200 && sorted[j].StartTs - sorted[i].StartTs <= window; j++)
            {
                var b = sorted[j].Source;
                if (b == a || !seen.Add(b)) continue;
                var key = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
                pairs[key] = pairs.GetValueOrDefault(key) + 1;
            }
        }
        return pairs.Select(p =>
            {
                var (sa, sb) = p.Key; var ca = counts[sa]; var cb = counts[sb];
                var ratio = Math.Min(1, p.Value / (double)Math.Min(ca, cb));
                return new AlarmConsequentialPair(sa, last[sa].Message, last[sa].RuleId, ca, sb, last[sb].Message, last[sb].RuleId, cb, p.Value, R(ratio, 3));
            })
            .Where(p => p.Together >= th.ConsequentialMinCount && p.Ratio >= th.ConsequentialMinRatio)
            .OrderByDescending(p => p.Together).ThenBy(p => p.SourceA, StringComparer.Ordinal).ThenBy(p => p.SourceB, StringComparer.Ordinal).Take(50).ToList();
    }

    // 合理化建议：只给出建议值与依据，从不自动修改规则。
    public static List<AlarmSuggestion> Suggest(AlarmAnalyticsResult result, IReadOnlyList<AlarmSample> samples, IReadOnlyDictionary<int, AlarmRuleConfig> rules, AlarmAnalyticsThresholds th)
    {
        var list = new List<AlarmSuggestion>();
        var days = Math.Max(result.Hours / 24, 1 / 24d);
        var inWindow = samples.Where(s => s.StartTs >= result.From && s.StartTs < result.To).ToList();
        Dictionary<string, double> Cur(int? ruleId)
        {
            if (ruleId is not int r || !rules.TryGetValue(r, out var c)) return new();
            return new() { ["delaySec"] = c.DelaySec, ["offDelaySec"] = c.OffDelaySec, ["recoverDeadband"] = c.RecoverDeadband, ["level"] = (int)c.Level };
        }
        string Lvl(int? ruleId, string fallback) => ruleId is int r && rules.TryGetValue(r, out var c) ? c.Level.ToString() : fallback;

        foreach (var c in result.ChatteringSources)
        {
            // 恢复后再次触发的间隔 P90 作为恢复延时建议（1～300 秒），同时建议加大恢复死区。
            var gaps = new List<double>();
            AlarmSample? prev = null;
            foreach (var s in inWindow.Where(s => s.Source == c.Source).OrderBy(s => s.StartTs))
            {
                if (prev?.EndTs is long e && s.StartTs >= e) gaps.Add((s.StartTs - e) / 1000d);
                prev = s;
            }
            var cur = Cur(c.RuleId);
            var suggestedOff = Math.Clamp(Math.Ceiling(Percentile(gaps, 0.9)), 1, 300);
            var suggested = new Dictionary<string, double>();
            if (suggestedOff > cur.GetValueOrDefault("offDelaySec")) suggested["offDelaySec"] = suggestedOff;
            list.Add(new("chattering", c.Source, c.RuleId, c.Message, Lvl(c.RuleId, c.Level), 3,
                new Dictionary<string, double> { ["episodes"] = c.Episodes, ["activations"] = c.Activations, ["maxInWindow"] = c.MaxInWindow, ["windowSec"] = th.ChatterWindowSec },
                cur, suggested));
        }
        foreach (var f in result.Fleeting.Where(f => f.Count >= 3 && f.Ratio >= 0.3))
        {
            var cur = Cur(f.RuleId);
            var on = Math.Clamp(Math.Ceiling(f.P90DurationSec) + 1, 1, 300);
            var suggested = new Dictionary<string, double>();
            if (on > cur.GetValueOrDefault("delaySec")) suggested["delaySec"] = on;
            list.Add(new("fleeting", f.Source, f.RuleId, f.Message, Lvl(f.RuleId, f.Level), 2,
                new Dictionary<string, double> { ["count"] = f.Count, ["ratio"] = f.Ratio, ["p90DurationSec"] = f.P90DurationSec }, cur, suggested));
        }
        foreach (var s in result.Standing.GroupBy(x => x.Source).Select(g => g.MaxBy(x => x.Hours)!))
            list.Add(new("standing", s.Source, s.RuleId, s.Message, Lvl(s.RuleId, s.Level), 2,
                new Dictionary<string, double> { ["hours"] = s.Hours, ["acked"] = s.Acked ? 1 : 0 }, Cur(s.RuleId), new Dictionary<string, double>()));
        var chatterSources = result.ChatteringSources.Select(x => x.Source).ToHashSet();
        foreach (var t in result.Top.Where(t => t.Count / days > th.FrequentPerDay && !chatterSources.Contains(t.Source)))
            list.Add(new("frequent", t.Source, t.RuleId, t.Message, Lvl(t.RuleId, t.Level), 2,
                new Dictionary<string, double> { ["count"] = t.Count, ["perDay"] = R(t.Count / days), ["percent"] = t.Percent }, Cur(t.RuleId), new Dictionary<string, double>()));
        // 优先级复核：高优先级占比超过目标时，列出贡献最大的高优先级报警；高优先级且频繁的报警也建议复核。
        var high = result.Priority.FirstOrDefault(p => p.Band == "high");
        foreach (var t in result.Top.Where(t => t.Level == nameof(AlarmLevel.Critical) && (t.Count / days > th.FrequentPerDay || (high is not null && high.Percent > high.TargetPercent * 1.5))))
            list.Add(new("priorityReview", t.Source, t.RuleId, t.Message, Lvl(t.RuleId, t.Level), 2,
                new Dictionary<string, double> { ["count"] = t.Count, ["highPercent"] = high?.Percent ?? 0, ["targetHighPercent"] = high?.TargetPercent ?? th.TargetHighPercent }, Cur(t.RuleId), new Dictionary<string, double>()));
        foreach (var p in result.Consequential)
            list.Add(new("consequential", p.SourceB, p.RuleB, p.MessageB, Lvl(p.RuleB, ""), 1,
                new Dictionary<string, double> { ["together"] = p.Together, ["ratio"] = p.Ratio, ["ruleA"] = p.RuleA ?? 0, ["windowSec"] = th.ConsequentialWindowSec },
                Cur(p.RuleB), new Dictionary<string, double>()));
        return list.OrderByDescending(x => x.Severity).ThenBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Source, StringComparer.Ordinal).ToList();
    }

    // 预聚合：把开始于 [from,to) 的报警按 (桶, 源, 等级) 汇总。bucketOf 把开始时刻映射到桶起点（小时或本地日）。
    public static List<AlarmRollup> Rollup(IEnumerable<AlarmSample> samples, long from, long to, int granularity, Func<long, long> bucketOf, AlarmAnalyticsThresholds th)
    {
        return samples.Where(s => s.StartTs >= from && s.StartTs < to)
            .GroupBy(s => (Bucket: bucketOf(s.StartTs), s.Source, s.Level))
            .Select(g =>
            {
                var last = g.OrderBy(s => s.StartTs).Last();
                var ann = g.Where(s => s.Annunciated).ToList();
                var acks = ann.Where(s => s.AckTs is long a && a >= s.StartTs).ToList();
                var clears = ann.Where(s => s.EndTs is long e && e >= s.StartTs).ToList();
                return new AlarmRollup
                {
                    Granularity = granularity, BucketTs = g.Key.Bucket, Source = g.Key.Source, Level = g.Key.Level,
                    RuleId = last.RuleId, TagId = last.TagId, DeviceId = last.DeviceId, GatewayId = last.GatewayId,
                    Message = last.Message.Length > 200 ? last.Message[..200] : last.Message,
                    Count = g.Count(), Annunciated = ann.Count, Shelved = g.Count(s => s.Shelved), Suppressed = g.Count(s => s.Suppressed), Chattering = g.Count(s => s.Chattering),
                    Fleeting = g.Count(s => IsFleeting(s, th)),
                    AckCount = acks.Count, AckSumMs = acks.Sum(s => s.AckTs!.Value - s.StartTs),
                    ClearCount = clears.Count, ClearSumMs = clears.Sum(s => s.EndTs!.Value - s.StartTs)
                };
            }).ToList();
    }
}
