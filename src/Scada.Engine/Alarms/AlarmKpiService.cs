using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;

namespace Scada.Engine.Alarms;

public sealed record AlarmFloodStatus(int Count, int Threshold, bool InFlood, long WindowStartTs, long WindowEndTs);

public sealed record AlarmTopItem(int? RuleId, int? TagId, int? DeviceId, string Message, string Level, int Count, int Chattering);
public sealed record AlarmStandingItem(int Id, int? RuleId, int? TagId, string Message, string Level, long StartTs, double Hours, bool Acked);
public sealed record AlarmOperatorItem(string User, int Acks, double AcksPerHour, double AvgAckSeconds, int Notes);
public sealed record AlarmFloodBucket(long StartTs, int Count);

public sealed record AlarmKpi(
    long From, long To, double Hours,
    int Total, int Annunciated, int Shelved, int Suppressed, int Chattering,
    double PerHour, double Per10Min, int Peak10Min, int FloodThreshold,
    int FloodBuckets, double PercentTimeInFlood, IReadOnlyList<AlarmFloodBucket> Floods,
    double OperatorPositions, double PerOperatorHour,
    IReadOnlyDictionary<string, int> ByLevel,
    IReadOnlyList<AlarmTopItem> Top,
    IReadOnlyList<AlarmStandingItem> Standing,
    IReadOnlyList<AlarmOperatorItem> Operators);

// 报警 KPI（参照 ISA-18.2 / EEMUA 191）：每小时/每 10 分钟报警数、洪水区间占比、最频繁报警、长期驻留报警、操作员确认负荷。
// 调用方传入可见性过滤（区域授权），统计只覆盖用户有权查看的报警。
public sealed class AlarmKpiService(IDbContextFactory<ConfigDbContext> factory, IOptions<ScadaSettings> options)
{
    public const long Bucket = 10 * 60_000L;
    private const int MaxRows = 200_000;
    private readonly ScadaSettings _settings = options.Value;

    public async Task<AlarmFloodStatus> FloodAsync(Func<AlarmEvent, bool> visible, long? nowMs = null, CancellationToken ct = default)
    {
        var now = nowMs ?? ScadaTime.NowMs();
        var from = now - Bucket;
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.AlarmEvents.AsNoTracking().Where(x => x.StartTs >= from && x.StartTs <= now && !x.Shelved && !x.Suppressed).ToListAsync(ct);
        var count = rows.Count(visible);
        var threshold = Math.Max(_settings.AlarmFloodThreshold, 1);
        return new(count, threshold, count > threshold, from, now);
    }

    public async Task<AlarmKpi> ComputeAsync(long from, long to, int top, double? standingHours, Func<AlarmEvent, bool> visible, CancellationToken ct = default)
    {
        if (to <= from) to = from + 1;
        await using var db = await factory.CreateDbContextAsync(ct);
        var events = (await db.AlarmEvents.AsNoTracking().Where(x => x.StartTs >= from && x.StartTs < to)
            .OrderBy(x => x.StartTs).Take(MaxRows).ToListAsync(ct)).Where(visible).ToList();
        var hours = (to - from) / 3_600_000d;
        var annunciated = events.Where(AlarmStates.Annunciated).ToList();
        var threshold = Math.Max(_settings.AlarmFloodThreshold, 1);
        var buckets = annunciated.GroupBy(x => from + (x.StartTs - from) / Bucket * Bucket)
            .Select(g => new AlarmFloodBucket(g.Key, g.Count())).OrderBy(x => x.StartTs).ToList();
        var floods = buckets.Where(x => x.Count > threshold).ToList();
        var bucketCount = Math.Max(1, (int)Math.Ceiling((to - from) / (double)Bucket));

        var topItems = events.GroupBy(x => x.RuleId is int r ? $"r{r}" : x.TagId is int t ? $"t{t}" : x.DeviceId is int d ? $"d{d}:{x.Message}" : $"m{x.Message}")
            .Select(g =>
            {
                var last = g.Last();
                return new AlarmTopItem(last.RuleId, last.TagId, last.DeviceId, last.Message, last.Level.ToString(), g.Count(), g.Count(x => x.Chattering));
            })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Message).Take(Math.Clamp(top, 1, 100)).ToList();

        // 长期驻留：当前仍活动且持续超过阈值的报警，不受统计时间窗限制。
        var now = ScadaTime.NowMs();
        var standingMs = (long)(Math.Max(standingHours ?? _settings.AlarmStandingHours, 0.01) * 3_600_000);
        var standing = (await db.AlarmEvents.AsNoTracking().Where(x => x.EndTs == null && x.StartTs <= now - standingMs)
                .OrderBy(x => x.StartTs).Take(5000).ToListAsync(ct))
            .Where(visible)
            .Select(x => new AlarmStandingItem(x.Id, x.RuleId, x.TagId, x.Message, x.Level.ToString(), x.StartTs, Math.Round((now - x.StartTs) / 3_600_000d, 2), x.AckTs is not null))
            .ToList();

        var acked = events.Where(x => x.AckTs is long ack && ack >= from && ack < to && !string.IsNullOrEmpty(x.AckUser)).ToList();
        var ids = events.Select(x => x.Id).ToHashSet();
        var notes = (await db.AlarmActivities.AsNoTracking().Where(x => x.Ts >= from && x.Ts < to && x.Action == "comment").ToListAsync(ct))
            .Where(x => ids.Contains(x.AlarmEventId) && x.User is not null).GroupBy(x => x.User!).ToDictionary(g => g.Key, g => g.Count());
        var operators = acked.GroupBy(x => x.AckUser!)
            .Select(g => new AlarmOperatorItem(g.Key, g.Count(), Math.Round(g.Count() / hours, 2), Math.Round(g.Average(x => (x.AckTs!.Value - x.StartTs) / 1000d), 1), notes.GetValueOrDefault(g.Key)))
            .OrderByDescending(x => x.Acks).ToList();
        // “每操作员每小时报警数”：报警量除以实际确认过报警的操作员人数（至少 1 人），反映单人负荷。
        var positions = Math.Max(1, operators.Count);

        return new AlarmKpi(
            from, to, Math.Round(hours, 3),
            events.Count, annunciated.Count, events.Count(x => x.Shelved), events.Count(x => x.Suppressed), events.Count(x => x.Chattering),
            Math.Round(annunciated.Count / hours, 2), Math.Round(annunciated.Count / (double)bucketCount, 2), buckets.Count == 0 ? 0 : buckets.Max(x => x.Count), threshold,
            floods.Count, Math.Round(floods.Count * 100d / bucketCount, 2), floods,
            positions, Math.Round(annunciated.Count / hours / positions, 2),
            events.GroupBy(x => x.Level.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            topItems, standing, operators);
    }
}
