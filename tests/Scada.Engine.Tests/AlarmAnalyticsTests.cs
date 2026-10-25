using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Alarms;

namespace Scada.Engine.Tests;

// 报警统计分析（ISA-18.2 / EEMUA 191）：用构造的事件序列逐项验证 KPI 与检测器，含跨零点/跨月、窗口开始前已活动、暂挂等边界。
public class AlarmAnalyticsTests
{
    // 2026-02-01 00:00 北京时间（UTC 2026-01-31 16:00）：既是本地零点也是跨月边界。
    private static readonly long T0 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
    private const long S = 1000, M = 60_000, H = 3_600_000;
    private static int _id;

    private static AlarmSample A(int rule, long start, long? end = null, long? ack = null, AlarmLevel level = AlarmLevel.Warning,
        bool shelved = false, bool suppressed = false, bool chattering = false, string? user = "op1") =>
        new(Interlocked.Increment(ref _id), rule, rule, 1, null, level, start, end, ack, ack is null ? null : user, shelved, suppressed, chattering, $"R{rule}");

    private static AlarmAnalyticsThresholds Th(Action<AlarmAnalyticsThresholds>? edit = null)
    {
        var th = new AlarmAnalyticsThresholds(); edit?.Invoke(th); return th;
    }

    [Fact]
    public void RatesPerOperatorAndEemuaRating()
    {
        var samples = Enumerable.Range(0, 12).Select(i => A(1, T0 + i * 5 * M)).ToList();
        samples.Add(A(2, T0 + M, shelved: true)); samples.Add(A(2, T0 + 2 * M, shelved: true)); samples.Add(A(3, T0 + 3 * M, suppressed: true));
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + H, T0 + 2 * H, Th());
        Assert.Equal(15, r.Total); Assert.Equal(12, r.Annunciated); Assert.Equal(2, r.Shelved); Assert.Equal(1, r.Suppressed);
        Assert.Equal(12, r.PerOperatorHour); Assert.Equal(2, r.PerOperator10Min); Assert.Equal(288, r.PerOperatorDay);
        Assert.Equal("manageable", r.Rating);

        var two = AlarmAnalytics.Analyze(samples, [], T0, T0 + H, T0 + 2 * H, Th(t => t.OperatorPositions = 2));
        Assert.Equal(6, two.PerOperatorHour); Assert.Equal(1, two.PerOperator10Min); Assert.Equal("acceptable", two.Rating);
    }

    [Fact]
    public void ZeroPositionsUsesDistinctAckers()
    {
        var samples = new[] { A(1, T0, ack: T0 + S, user: "a"), A(1, T0 + M, ack: T0 + 2 * M, user: "b"), A(1, T0 + 3 * M), A(1, T0 + 4 * M) };
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + H, T0 + H, Th(t => t.OperatorPositions = 0));
        Assert.Equal(2, r.OperatorPositions); Assert.Equal(2, r.PerOperatorHour);
    }

    [Fact]
    public void WindowIsHalfOpenAndEarlierAlarmsAreNotCounted()
    {
        var samples = new[] { A(1, T0 - S), A(1, T0), A(1, T0 + H - 1), A(1, T0 + H) };
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + H, T0 + 2 * H, Th());
        Assert.Equal(2, r.Total);
    }

    [Fact]
    public void FloodDetectionUsesHysteresisAndSpansMidnightAndMonth()
    {
        // 10 分钟桶计数 [2, 12 | 7, 4, 11, 3]，T0（跨月零点）位于第 3 个桶开始处。
        var from = T0 - 20 * M;
        int[] counts = [2, 12, 7, 4, 11, 3];
        var samples = new List<AlarmSample>();
        for (var b = 0; b < counts.Length; b++)
            for (var i = 0; i < counts[b]; i++) samples.Add(A(100 + i, from + b * 10 * M + i * 20 * S + 10 * S));
        var r = AlarmAnalytics.Analyze(samples, [], from, from + 60 * M, from + 2 * H, Th());
        Assert.Equal(2, r.Floods.Count);
        var first = r.Floods[0];
        Assert.Equal(from + 10 * M, first.StartTs); Assert.Equal(from + 30 * M, first.EndTs); // 跨过 T0 仍是一个洪水区间
        Assert.True(first.StartTs < T0 && first.EndTs > T0);
        Assert.Equal(19, first.Count); Assert.Equal(12, first.Peak10Min);
        Assert.Equal(from + 10 * M + 10 * S, first.FirstOut!.StartTs);
        Assert.Equal("R100", first.FirstOut.Message);
        Assert.Equal(19, first.EventIds.Count);
        Assert.Equal(from + 40 * M, r.Floods[1].StartTs); Assert.Equal(from + 50 * M, r.Floods[1].EndTs);
        Assert.Equal(50, r.PercentTimeInFlood);
        Assert.Equal(12, r.Peak10Min); Assert.Equal(from + 10 * M, r.Peak10MinTs);
        Assert.Equal(33.33, r.PctBucketsFlood);
        // 两个岗位：每操作员 6 条，不构成洪水。
        Assert.Empty(AlarmAnalytics.Analyze(samples, [], from, from + 60 * M, from + 2 * H, Th(t => t.OperatorPositions = 2)).Floods);
    }

    [Fact]
    public void FloodStillOpenAtWindowEndIsClosedAtLastBucket()
    {
        var samples = Enumerable.Range(0, 11).Select(i => A(1, T0 + i * S)).ToList();
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + 10 * M, T0 + H, Th());
        var flood = Assert.Single(r.Floods);
        Assert.Equal(T0 + 10 * M, flood.EndTs); Assert.Equal(100, r.PercentTimeInFlood);
    }

    [Fact]
    public void ShelvedSuppressedAndChatteringDoNotCountTowardsRateOrFlood()
    {
        var samples = Enumerable.Range(0, 20).Select(i => A(1, T0 + i * S, shelved: i % 3 == 0, suppressed: i % 3 == 1, chattering: i % 3 == 2)).ToList();
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + 10 * M, T0 + H, Th());
        Assert.Equal(0, r.Annunciated); Assert.Empty(r.Floods); Assert.Equal(20, r.Total);
    }

    [Fact]
    public void AckAndClearAverageAndMedian()
    {
        var samples = new[]
        {
            A(1, T0, end: T0 + 30 * S, ack: T0 + 10 * S),
            A(2, T0 + M, end: T0 + M + 90 * S, ack: T0 + M + 20 * S),
            A(3, T0 + 2 * M, ack: T0 + 2 * M + 60 * S),
            A(4, T0 + 3 * M, end: T0 + 4 * M, ack: T0 + 3 * M + 999 * S, shelved: true) // 暂挂的不计入响应时间
        };
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + H, T0 + H, Th());
        Assert.Equal(3, r.AckCount); Assert.Equal(30, r.AvgAckSec); Assert.Equal(20, r.MedianAckSec);
        Assert.Equal(2, r.ClearCount); Assert.Equal(60, r.AvgClearSec); Assert.Equal(60, r.MedianClearSec);
        Assert.Null(AlarmAnalytics.Analyze([], [], T0, T0 + H, T0 + H, Th()).AvgAckSec);
    }

    [Fact]
    public void StandingAndStaleIncludeAlarmsActiveBeforeWindowStart()
    {
        var unacked = A(1, T0 - 30 * H);
        var acked = A(2, T0 - 25 * H, ack: T0 - 24 * H);
        var endedBefore = A(3, T0 - 48 * H, end: T0 - H);
        var endedInside = A(4, T0 - 26 * H, end: T0 + 30 * M);
        var young = A(5, T0 - H);
        var r = AlarmAnalytics.Analyze([unacked, acked, endedBefore, endedInside, young], [], T0, T0 + H, T0 + 5 * H, Th());
        Assert.Equal(0, r.Total);
        Assert.Equal([unacked.Id, acked.Id], r.Standing.Select(x => x.Id));
        Assert.Equal([unacked.Id], r.Stale.Select(x => x.Id));
        Assert.Equal(31, r.Standing[0].Hours); // 观察点为窗口结束 T0+1h
        Assert.Equal(3, r.StandingDuringWindow);
        // 当前时刻早于窗口结束：以当前时刻为观察点。
        var early = AlarmAnalytics.Analyze([acked], [], T0 - 2 * H, T0 + H, T0 - H - 30 * M, Th());
        Assert.Empty(early.Standing);
    }

    [Fact]
    public void ShelvedAndSuppressedDurationsAreClippedToWindow()
    {
        ShelveInterval[] shelves = [new(1, T0 - H, T0 + 30 * M), new(2, T0 + 50 * M, T0 + 2 * H), new(3, T0 - 3 * H, T0 - H)];
        var suppressed = A(9, T0 + 30 * M, suppressed: true);
        var suppressedEarly = A(9, T0 - 2 * H, end: T0 + 15 * M, suppressed: true);
        var r = AlarmAnalytics.Analyze([suppressed, suppressedEarly], shelves, T0, T0 + H, T0 + 3 * H, Th());
        Assert.Equal(2, r.ShelveCount);
        Assert.Equal(0.67, r.ShelvedHours);
        Assert.Equal(1, r.ShelvedByRule[0].RuleId); Assert.Equal(0.5, r.ShelvedByRule[0].Hours);
        Assert.Equal(0.75, r.SuppressedHours);
    }

    [Fact]
    public void ChatterEpisodesAreNonOverlappingAndWindowInclusive()
    {
        Assert.Equal((2, 4), AlarmAnalytics.ChatterEpisodes([0, 20 * S, 40 * S, 50 * S, 200 * S, 210 * S, 215 * S], 3, 60 * S));
        Assert.Equal((1, 3), AlarmAnalytics.ChatterEpisodes([0, 30 * S, 60 * S], 3, 60 * S));
        Assert.Equal((0, 2), AlarmAnalytics.ChatterEpisodes([0, 30 * S, 61 * S], 3, 60 * S));
        Assert.Equal((0, 0), AlarmAnalytics.ChatterEpisodes([], 3, 60 * S));
    }

    [Fact]
    public void ChatteringSourcesAndBadActorPareto()
    {
        var samples = new List<AlarmSample>();
        foreach (var t in new[] { 0, 20, 40, 300, 310, 320 }) samples.Add(A(1, T0 + t * S, end: T0 + t * S + 5 * S));
        samples.Add(A(2, T0 + 10 * M)); samples.Add(A(2, T0 + 30 * M)); samples.Add(A(3, T0 + 40 * M)); samples.Add(A(4, T0 + 50 * M));
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + H, T0 + H, Th(t => t.TopN = 2));
        var c = Assert.Single(r.ChatteringSources);
        Assert.Equal("r1", c.Source); Assert.Equal(2, c.Episodes); Assert.Equal(6, c.Activations);
        Assert.Equal(["r1", "r2"], r.Top.Select(x => x.Source));
        Assert.Equal(60, r.Top[0].Percent); Assert.Equal(80, r.Top[1].CumulativePercent);
        Assert.Equal(2, r.Top[0].ChatterEpisodes); Assert.Equal(6, r.Top[0].Fleeting);
    }

    [Fact]
    public void FleetingRequiresClearWithinLimitBeforeAck()
    {
        var th = Th(t => t.FleetingSec = 10);
        Assert.True(AlarmAnalytics.IsFleeting(A(1, T0, end: T0 + 5 * S), th));
        Assert.True(AlarmAnalytics.IsFleeting(A(1, T0, end: T0 + 10 * S), th));      // 恰好等于上限
        Assert.False(AlarmAnalytics.IsFleeting(A(1, T0, end: T0 + 11 * S), th));
        Assert.False(AlarmAnalytics.IsFleeting(A(1, T0, end: T0 + 5 * S, ack: T0 + 3 * S), th)); // 恢复前已确认
        Assert.True(AlarmAnalytics.IsFleeting(A(1, T0, end: T0 + 5 * S, ack: T0 + 30 * S), th)); // 恢复后才确认
        Assert.False(AlarmAnalytics.IsFleeting(A(1, T0), th));                        // 仍活动

        var samples = new[] { A(7, T0, end: T0 + 2 * S), A(7, T0 + M, end: T0 + M + 4 * S), A(7, T0 + 2 * M, end: T0 + 2 * M + 8 * S), A(7, T0 + 3 * M, end: T0 + 4 * M) };
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + H, T0 + H, th);
        var f = Assert.Single(r.Fleeting);
        Assert.Equal(3, f.Count); Assert.Equal(4, f.Total); Assert.Equal(0.75, f.Ratio); Assert.Equal(8, f.P90DurationSec);
        Assert.Equal(3, r.FleetingTotal);
    }

    [Fact]
    public void ConsequentialPairsWithinWindow()
    {
        var samples = new List<AlarmSample>();
        for (var i = 0; i < 6; i++) { samples.Add(A(1, T0 + i * 10 * M)); samples.Add(A(2, T0 + i * 10 * M + 2 * S)); }
        samples.Add(A(2, T0 + 5 * M)); samples.Add(A(2, T0 + 15 * M)); samples.Add(A(3, T0 + 25 * M));
        var sorted = samples.OrderBy(x => x.StartTs).ToList();
        var pair = Assert.Single(AlarmAnalytics.Consequential(sorted, Th()));
        Assert.Equal(("r1", "r2"), (pair.SourceA, pair.SourceB));
        Assert.Equal(6, pair.Together); Assert.Equal(6, pair.CountA); Assert.Equal(8, pair.CountB); Assert.Equal(1, pair.Ratio);
        Assert.Empty(AlarmAnalytics.Consequential(sorted, Th(t => t.ConsequentialWindowSec = 1)));
        Assert.Empty(AlarmAnalytics.Consequential(sorted, Th(t => t.ConsequentialMinCount = 7)));
    }

    [Fact]
    public void PriorityDistributionAgainstTargets()
    {
        var samples = Enumerable.Range(0, 8).Select(i => A(1, T0 + i * M, level: AlarmLevel.Info)).ToList();
        samples.Add(A(2, T0 + 20 * M, level: AlarmLevel.Warning)); samples.Add(A(3, T0 + 21 * M, level: AlarmLevel.Critical));
        samples.Add(A(3, T0 + 22 * M, level: AlarmLevel.Critical, shelved: true)); // 暂挂的不计入分布
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + H, T0 + H, Th());
        Assert.Equal([("low", 80.0, 80.0), ("medium", 10.0, 15.0), ("high", 10.0, 5.0)], r.Priority.Select(p => (p.Band, p.Percent, p.TargetPercent)));
    }

    [Fact]
    public void SuggestionsAreRecommendationsOnly()
    {
        var samples = new List<AlarmSample>();
        // 规则 1：抖动，恢复后 4～8 秒再次触发。
        long t = T0;
        for (var i = 0; i < 6; i++) { samples.Add(A(1, t, end: t + 15 * S, ack: t + S)); t += 15 * S + (4 + i % 3 * 2) * S; }
        // 规则 2：瞬时报警（2～6 秒恢复、无人确认）。
        for (var i = 0; i < 4; i++) samples.Add(A(2, T0 + 10 * M + i * 5 * M, end: T0 + 10 * M + i * 5 * M + (2 + i) * S));
        // 规则 3：长期驻留；规则 4：高优先级频繁。
        samples.Add(A(3, T0 - 30 * H));
        for (var i = 0; i < 12; i++) samples.Add(A(4, T0 + i * 4 * M + 30 * S, level: AlarmLevel.Critical, ack: T0 + i * 4 * M + 40 * S));
        var rule1 = new AlarmRuleConfig(1, "R1", AlarmLevel.Warning, 0, 2, 0.5);
        var rules = new Dictionary<int, AlarmRuleConfig> { [1] = rule1, [2] = new(2, "R2", AlarmLevel.Warning, 3, 0, 0), [4] = new(4, "R4", AlarmLevel.Critical, 0, 0, 0) };
        var th = Th(t2 => t2.FrequentPerDay = 100);
        var r = AlarmAnalytics.Analyze(samples, [], T0, T0 + H, T0 + H, th);
        var s = AlarmAnalytics.Suggest(r, samples, rules, th);
        var chatter = s.Single(x => x.Kind == "chattering");
        Assert.Equal(1, chatter.RuleId); Assert.Equal(2, chatter.Current["offDelaySec"]); Assert.Equal(8, chatter.Suggested["offDelaySec"]);
        var fleeting = s.Single(x => x.Kind == "fleeting");
        Assert.Equal(2, fleeting.RuleId); Assert.Equal(6, fleeting.Suggested["delaySec"]); // P90=5s → 6s
        Assert.Contains(s, x => x.Kind == "standing" && x.RuleId == 3);
        Assert.Contains(s, x => x.Kind == "frequent" && x.RuleId == 4);         // 12/h ≈ 288/天 > 100
        Assert.Contains(s, x => x.Kind == "priorityReview" && x.RuleId == 4);
        Assert.DoesNotContain(s, x => x.Kind == "frequent" && x.RuleId == 1);  // 抖动源不重复给“频繁”
        // 建议不修改规则配置。
        Assert.Equal(new AlarmRuleConfig(1, "R1", AlarmLevel.Warning, 0, 2, 0.5), rules[1]);
        // 当前恢复延时已足够时不给新值。
        var enough = AlarmAnalytics.Suggest(r, samples, new Dictionary<int, AlarmRuleConfig> { [1] = rule1 with { OffDelaySec = 60 } }, th);
        Assert.Empty(enough.Single(x => x.Kind == "chattering").Suggested);
    }

    [Fact]
    public void ThresholdValidation()
    {
        Assert.Null(new AlarmAnalyticsThresholds().Validate());
        Assert.NotNull(Th(t => t.FloodEndPer10Min = 20).Validate());
        Assert.NotNull(Th(t => t.TargetHighPercent = 10).Validate());
        Assert.NotNull(Th(t => t.ChatterCount = 1).Validate());
        Assert.NotNull(Th(t => t.ConsequentialMinRatio = 0).Validate());
    }

    // ---------- 预聚合与服务（SQLite） ----------

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }

    private static async Task<(SqliteConnection, Factory, AlarmAnalyticsService, AlarmRollupService)> Setup(ScadaSettings? settings = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        settings ??= new ScadaSettings();
        settings.ShiftTimeZone ??= "Asia/Shanghai";
        var options = Options.Create(settings);
        var analytics = new AlarmAnalyticsService(factory, options);
        var services = new ServiceCollection().AddSingleton(analytics).BuildServiceProvider();
        var rollup = new AlarmRollupService(services, factory, options, NullLogger<AlarmRollupService>.Instance);
        return (connection, factory, analytics, rollup);
    }

    private static AlarmEvent E(int rule, long start, long? end = null, long? ack = null, AlarmLevel level = AlarmLevel.Warning, bool shelved = false) =>
        new() { RuleId = rule, TagId = rule, Level = level, StartTs = start, EndTs = end, AckTs = ack, AckUser = ack is null ? null : "op", Message = $"R{rule}", Shelved = shelved };

    [Fact]
    public async Task DailyRollupsSplitAtLocalMidnightAcrossMonth()
    {
        var (connection, factory, analytics, rollup) = await Setup();
        await using var _ = connection;
        await using (var db = factory.CreateDbContext())
        {
            db.AlarmEvents.AddRange(E(1, T0 - M), E(1, T0 - 2 * M), E(1, T0 + M), E(2, T0 + 2 * M, level: AlarmLevel.Critical));
            await db.SaveChangesAsync();
        }
        await rollup.RunOnceAsync(T0 + 3 * H + 5 * M, default);
        await using (var db = factory.CreateDbContext())
        {
            var days = await db.AlarmRollups.Where(x => x.Granularity == AlarmRollup.Day).OrderBy(x => x.BucketTs).ThenBy(x => x.Source).ToListAsync();
            Assert.Equal([(T0 - 24 * H, "r1", 2), (T0, "r1", 1), (T0, "r2", 1)], days.Select(x => (x.BucketTs, x.Source, x.Count)));
            var hours = await db.AlarmRollups.Where(x => x.Granularity == AlarmRollup.Hour).ToListAsync();
            Assert.Equal(2, hours.Single(x => x.BucketTs == T0 - H).Count);
            Assert.Equal(T0 + 3 * H, (await db.AlarmRollupStates.SingleAsync()).WatermarkTs);
        }
        var (granularity, points) = await analytics.TrendAsync(T0 - 24 * H * 2, T0 + 24 * H, _ => true, T0 + 3 * H);
        Assert.Equal("day", granularity);
        Assert.Equal([0, 2, 2], points.Select(p => p.Total));
        Assert.Equal(T0, points[2].Ts);
    }

    [Fact]
    public async Task RollupRecomputesRecentHoursForLateAckAndAppliesRetention()
    {
        var (connection, factory, analytics, rollup) = await Setup(new ScadaSettings { AlarmRollupRecomputeHours = 2, AlarmRollupHourlyRetentionDays = 2, AlarmRollupDailyRetentionDays = 5 });
        await using var _ = connection;
        int id;
        await using (var db = factory.CreateDbContext())
        {
            var e = E(1, T0 + 10 * M); db.AlarmEvents.Add(e);
            db.AlarmEvents.Add(E(2, T0 - 10 * 24 * H));
            await db.SaveChangesAsync(); id = e.Id;
        }
        await rollup.RunOnceAsync(T0 + H + M, default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(0, (await db.AlarmRollups.SingleAsync(x => x.Granularity == AlarmRollup.Hour && x.Source == "r1")).AckCount);
            // 回补起点为最早报警，但 10 天前的行超出保留期被清理。
            Assert.False(await db.AlarmRollups.AnyAsync(x => x.Source == "r2"));
            var e = await db.AlarmEvents.SingleAsync(x => x.Id == id); e.AckTs = T0 + 70 * M; e.AckUser = "op"; await db.SaveChangesAsync();
        }
        await rollup.RunOnceAsync(T0 + 2 * H + M, default);
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.AlarmRollups.SingleAsync(x => x.Granularity == AlarmRollup.Hour && x.Source == "r1");
            Assert.Equal(1, row.AckCount); Assert.Equal(60 * M, row.AckSumMs);
            Assert.Equal(1, (await db.AlarmRollups.SingleAsync(x => x.Granularity == AlarmRollup.Day && x.Source == "r1")).AckCount);
        }
    }

    [Fact]
    public async Task LongRangeMergesRollupsWithDetailWindow()
    {
        var (connection, factory, analytics, rollup) = await Setup(new ScadaSettings { AlarmAnalyticsRawMaxDays = 7, AlarmRollupBackfillDays = 60 });
        await using var _ = connection;
        var events = new List<AlarmEvent>();
        for (var d = 0; d < 40; d++)
            for (var k = 0; k < 3; k++) events.Add(E(1 + k, T0 - d * 24 * H + k * 7 * M, end: T0 - d * 24 * H + k * 7 * M + 30 * S, ack: T0 - d * 24 * H + k * 7 * M + (10 + k) * S, level: (AlarmLevel)k, shelved: d % 10 == 0 && k == 2));
        await using (var db = factory.CreateDbContext()) { db.AlarmEvents.AddRange(events); await db.SaveChangesAsync(); }
        var now = T0 + 6 * H;
        await rollup.RunOnceAsync(now, default);
        var from = T0 - 40 * 24 * H; var to = T0 + 24 * H;
        var report = await analytics.AnalyzeAsync(from, to, _ => true, now);
        Assert.True(report.UsedRollups);
        Assert.True(report.DetailFrom > from);
        var exact = AlarmAnalytics.Analyze(events.Select(AlarmSample.From).ToList(), [], from, to, now, report.Thresholds);
        Assert.Equal(exact.Total, report.Kpi.Total);
        Assert.Equal(exact.Annunciated, report.Kpi.Annunciated);
        Assert.Equal(exact.Shelved, report.Kpi.Shelved);
        Assert.Equal(exact.AvgAckSec, report.Kpi.AvgAckSec);
        Assert.Equal(exact.AvgClearSec, report.Kpi.AvgClearSec);
        Assert.Equal(exact.PerOperatorHour, report.Kpi.PerOperatorHour);
        Assert.Equal(exact.Priority, report.Kpi.Priority);
        Assert.Equal(exact.Top.Select(x => (x.Source, x.Count, x.Percent)), report.Kpi.Top.Select(x => (x.Source, x.Count, x.Percent)));
        // 区域/可见性过滤同样作用于预聚合部分。
        var onlyRule1 = await analytics.AnalyzeAsync(from, to, x => x.RuleId == 1, now);
        Assert.Equal(40, onlyRule1.Kpi.Total);
        Assert.Equal(40, (await analytics.TrendAsync(from, to, x => x.RuleId == 1, now)).Points.Sum(p => p.Total));
    }

    [Fact]
    public async Task ThresholdsFallBackToDefaultsWhenStoredJsonInvalid()
    {
        var (connection, factory, analytics, _) = await Setup(new ScadaSettings { AlarmFloodThreshold = 20 });
        await using var __ = connection;
        Assert.Equal(20, (await analytics.GetThresholdsAsync()).Thresholds.FloodStartPer10Min);
        await using (var db = factory.CreateDbContext())
        {
            db.AlarmAnalyticsConfigs.Add(new AlarmAnalyticsConfig { Id = 1, ThresholdsJson = "{\"fleetingSec\":30}" }); await db.SaveChangesAsync();
        }
        var th = (await analytics.GetThresholdsAsync()).Thresholds;
        Assert.Equal(30, th.FleetingSec); Assert.Equal(20, th.FloodStartPer10Min);
        Assert.Equal(10, analytics.Parse("{\"fleetingSec\":-5}").FleetingSec);
        Assert.Equal(10, analytics.Parse("not json").FleetingSec);
    }

    [Fact]
    public async Task DrillDownFiltersBySourceAndKind()
    {
        var (connection, factory, analytics, _) = await Setup();
        await using var __ = connection;
        await using (var db = factory.CreateDbContext())
        {
            db.AlarmEvents.AddRange(E(1, T0, end: T0 + 2 * S), E(1, T0 + M), E(2, T0 + 2 * M, shelved: true), E(2, T0 - M));
            await db.SaveChangesAsync();
        }
        Assert.Equal(3, (await analytics.EventsAsync(T0, T0 + H, _ => true, null, null, null, 100)).Total);
        Assert.Equal(2, (await analytics.EventsAsync(T0, T0 + H, _ => true, "r1", null, null, 100)).Total);
        Assert.Equal(1, (await analytics.EventsAsync(T0, T0 + H, _ => true, null, "fleeting", null, 100)).Total);
        Assert.Equal(1, (await analytics.EventsAsync(T0, T0 + H, _ => true, null, "shelved", null, 100)).Total);
        Assert.Equal(0, (await analytics.EventsAsync(T0, T0 + H, x => x.RuleId != 1 && x.RuleId != 2, null, null, null, 100)).Total);
    }
}
