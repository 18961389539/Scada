using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;

namespace Scada.Engine.Oee;

// 产线采样：读实时缓存中的状态/计数测点，开闭停机事件，把时间与产量增量写入分钟预聚合。备机不运行。
public sealed class OeeCollectorService(
    IServiceProvider services,
    IDbContextFactory<ConfigDbContext> factory,
    IRealtimeCache cache,
    IOptions<ScadaSettings> options,
    ILogger<OeeCollectorService> logger) : BackgroundService
{
    private readonly ScadaSettings _settings = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SampleOnceAsync(long now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await SampleCoreAsync(now, ct); }
        finally { _gate.Release(); }
    }

    private async Task SampleCoreAsync(long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var lines = await db.ProductionLines.Where(x => x.Enabled).ToListAsync(ct);
        if (lines.Count == 0) return;
        var products = await db.ProductionProducts.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var samples = await db.OeeLineSamples.ToDictionaryAsync(x => x.LineId, ct);
        var openEvents = await db.DowntimeEvents.Where(x => x.EndTs == null && x.Source == "tag").ToListAsync(ct);
        var openByLine = openEvents.GroupBy(x => x.LineId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.StartTs).First());

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            var state = LineState.Running;
            if (line.StateTagId is int stId)
            {
                var v = cache.Get(stId);
                state = OeeService.MapState(line.StateMapJson, v?.Quality == TagQuality.Good ? v.Value : null);
                if (state == LineState.Unknown) state = LineState.Running;
            }

            double? good = Read(line.GoodCountTagId), reject = Read(line.RejectCountTagId), total = Read(line.TotalCountTagId);
            samples.TryGetValue(line.Id, out var prev);
            var prevTs = prev?.Ts ?? now;
            var elapsed = Math.Max(0, now - prevTs);

            // 状态变化 → 开闭停机事件。
            var prevState = prev?.State ?? LineState.Running;
            if (state != prevState || (state != LineState.Running && !openByLine.ContainsKey(line.Id)))
            {
                if (openByLine.TryGetValue(line.Id, out var open) && (state == LineState.Running || state != open.State))
                {
                    open.EndTs = now; open.UpdatedTs = now; open.UpdatedBy = "oee-collector";
                    openByLine.Remove(line.Id);
                }
                if (state != LineState.Running)
                {
                    var ev = new DowntimeEvent
                    {
                        LineId = line.Id, StartTs = now, State = state, Source = "tag", Confirmed = true,
                        CreatedBy = "oee-collector", CreatedTs = now
                    };
                    db.DowntimeEvents.Add(ev);
                    openByLine[line.Id] = ev;
                }
            }

            // 计数增量（处理回绕：新值 < 旧值时按新值起算）。
            double dGood = 0, dReject = 0, dScrap = 0;
            if (prev is not null)
            {
                dGood = Delta(prev.GoodCount, good);
                dReject = Delta(prev.RejectCount, reject);
                if (total is not null && prev.TotalCount is not null)
                {
                    var dTotal = Delta(prev.TotalCount, total);
                    // 有总产量计数器时：废品 = 总增量 − 好品 − 已计废品（拒绝）。
                    var inferred = dTotal - dGood - dReject;
                    if (inferred > 0) dScrap = inferred;
                }
            }

            var ideal = OeeMath.EffectiveIdealCycleSec(line.RatedUnitsPerHour, line.IdealCycleSec);
            if (line.ProductId is int pid && products.TryGetValue(pid, out var prod))
                ideal = OeeMath.EffectiveIdealCycleSec(prod.RatedUnitsPerHour, prod.IdealCycleSec);
            var targetPerHour = line.TargetUnitsPerHour ?? 0;

            if (elapsed > 0 || dGood > 0 || dReject > 0 || dScrap > 0)
            {
                // 把 [prevTs, now) 按分钟切开写入预聚合。
                var cursor = prevTs;
                while (cursor < now)
                {
                    var bucket = OeeService.FloorMinute(cursor);
                    var next = Math.Min(now, bucket + OeeMath.MinuteMs);
                    var ms = next - cursor;
                    var frac = elapsed > 0 ? ms / (double)elapsed : 1;
                    var row = await db.OeeRollups.FirstOrDefaultAsync(x =>
                        x.Granularity == OeeRollup.Minute && x.BucketTs == bucket && x.LineId == line.Id && x.ProductId == line.ProductId, ct);
                    if (row is null)
                    {
                        row = new OeeRollup
                        {
                            Granularity = OeeRollup.Minute, BucketTs = bucket, LineId = line.Id, ProductId = line.ProductId,
                            IdealCycleSec = ideal
                        };
                        db.OeeRollups.Add(row);
                    }
                    // 整段 [prevTs, now) 记为变化前状态（状态变化发生在 now）。
                    var useState = prev is not null ? prevState : state;
                    switch (useState)
                    {
                        case LineState.Running: row.RunMs += ms; break;
                        case LineState.Fault: row.FaultMs += ms; row.DownMs += ms; break;
                        case LineState.Setup: row.SetupMs += ms; row.DownMs += ms; break;
                        default: row.DownMs += ms; break;
                    }
                    // 计划时间先整段累加；rollup 会删除该日分钟行并按班次重建，这里的值随后被覆盖。
                    row.PlannedMs += ms;
                    row.GoodCount += dGood * frac;
                    row.RejectCount += dReject * frac;
                    row.ScrapCount += dScrap * frac;
                    if (ideal > 0) row.IdealCycleSec = ideal;
                    row.TargetCount += targetPerHour * (ms / (double)OeeMath.HourMs);
                    cursor = next;
                }
            }

            if (prev is null)
            {
                prev = new OeeLineSample { LineId = line.Id };
                db.OeeLineSamples.Add(prev);
                samples[line.Id] = prev;
            }
            prev.Ts = now; prev.State = state; prev.GoodCount = good; prev.RejectCount = reject; prev.TotalCount = total; prev.ProductId = line.ProductId;
        }
        await db.SaveChangesAsync(ct);

        double? Read(int? tagId) => tagId is int id && cache.Get(id) is { Quality: TagQuality.Good } v && double.IsFinite(v.Value) ? v.Value : null;
        static double Delta(double? prev, double? cur)
        {
            if (prev is null || cur is null) return 0;
            return cur.Value >= prev.Value ? cur.Value - prev.Value : cur.Value;
        }
    }

    // 把报警导入为待确认停机候选（同报警不重复）。
    public async Task<int> ImportAlarmCandidatesAsync(long from, long to, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var minLevel = (AlarmLevel)Math.Clamp(_settings.OeeAlarmDowntimeMinLevel, 0, 3);
        var lines = await db.ProductionLines.AsNoTracking().Where(x => x.Enabled && x.StateTagId != null).ToListAsync(ct);
        var tagToLine = lines.Where(l => l.StateTagId is not null).ToDictionary(l => l.StateTagId!.Value, l => l.Id);
        if (tagToLine.Count == 0) return 0;
        var tagIds = tagToLine.Keys.ToList();
        var existing = (await db.DowntimeEvents.AsNoTracking().Where(x => x.Source == "alarm" && x.AlarmEventId != null && x.StartTs >= from && x.StartTs < to)
            .Select(x => x.AlarmEventId!.Value).ToListAsync(ct)).ToHashSet();
        var alarms = await db.AlarmEvents.AsNoTracking()
            .Where(x => x.TagId != null && tagIds.Contains(x.TagId.Value) && x.StartTs >= from && x.StartTs < to && x.Level >= minLevel)
            .OrderBy(x => x.StartTs).Take(5000).ToListAsync(ct);
        var n = 0;
        foreach (var a in alarms)
        {
            if (a.TagId is not int t || !tagToLine.TryGetValue(t, out var lineId) || existing.Contains(a.Id)) continue;
            db.DowntimeEvents.Add(new DowntimeEvent
            {
                LineId = lineId, StartTs = a.StartTs, EndTs = a.EndTs, State = LineState.Fault, Source = "alarm",
                AlarmEventId = a.Id, Note = a.Message, Confirmed = false, CreatedBy = "oee-alarm-import", CreatedTs = ScadaTime.NowMs()
            });
            n++;
        }
        if (n > 0) await db.SaveChangesAsync(ct);
        return n;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.OeeEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken).ContinueWith(_ => { });
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _settings.OeeSampleIntervalSec)));
        do
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (services.GetService<INodeRole>() is { IsActive: false }) continue;
            try { await SampleOnceAsync(ScadaTime.NowMs(), stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "OEE 采样失败");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => !t.IsCanceled && t.Result));
    }
}
