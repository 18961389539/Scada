using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;

namespace Scada.Engine.Oee;

// OEE 预聚合：分钟 → 小时 → 日；按班次校正 PlannedMs；回补与保留。备机不运行。
public sealed class OeeRollupService(
    IServiceProvider services,
    IDbContextFactory<ConfigDbContext> factory,
    IOptions<ScadaSettings> options,
    ILogger<OeeRollupService> logger) : BackgroundService
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
        var oee = services.GetRequiredService<OeeService>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var state = await db.OeeRollupStates.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (state is null) { state = new OeeRollupState { Id = 1 }; db.OeeRollupStates.Add(state); }

        var closedMin = OeeService.FloorMinute(now);
        long start;
        if (state.WatermarkTs is long w) start = Math.Max(0, w - Math.Max(1, _settings.OeeRollupRecomputeHours) * OeeMath.HourMs);
        else
        {
            var earliest = await db.OeeRollups.AsNoTracking().Where(x => x.Granularity == OeeRollup.Minute).MinAsync(x => (long?)x.BucketTs, ct)
                ?? await db.DowntimeEvents.AsNoTracking().MinAsync(x => (long?)x.StartTs, ct)
                ?? closedMin;
            start = Math.Max(earliest, closedMin - Math.Max(1, _settings.OeeRollupBackfillDays) * OeeService.DayMs);
        }
        if (state.RebuildFromTs is long rebuild) start = Math.Min(start, rebuild);

        var minuteCut = closedMin - Math.Max(1, _settings.OeeRollupMinuteRetentionDays) * OeeService.DayMs;
        var hourCut = closedMin - Math.Max(1, _settings.OeeRollupHourlyRetentionDays) * OeeService.DayMs;
        var dayCut = closedMin - Math.Max(1, _settings.OeeRollupDailyRetentionDays) * OeeService.DayMs;
        start = Math.Max(start, dayCut);

        var lines = await db.ProductionLines.AsNoTracking().ToListAsync(ct);
        var shifts = await db.ProductionShifts.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);
        var reasons = await db.DowntimeReasons.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var written = 0;

        for (var day = oee.DayStart(start); day < closedMin; day = oee.NextDay(day))
        {
            ct.ThrowIfCancellationRequested();
            var dayEnd = Math.Min(oee.NextDay(day), closedMin);
            var areaShifts = shifts; // 按产线区域过滤在循环内

            foreach (var line in lines)
            {
                var lineShifts = oee.ExpandShifts(shifts.Where(s => s.AreaId == line.AreaId).ToList(), day, dayEnd);
                // 从停机事件重建该日分钟行（覆盖采集器写入，保证回补一致），再汇总小时/日。
                var events = await db.DowntimeEvents.AsNoTracking()
                    .Where(x => x.LineId == line.Id && x.Confirmed && x.StartTs < dayEnd && (x.EndTs == null || x.EndTs > day))
                    .ToListAsync(ct);
                var intervals = OeeService.BuildIntervals(day, dayEnd, events, reasons);
                var corrections = await db.ProductionCountCorrections.AsNoTracking()
                    .Where(x => x.LineId == line.Id && x.PeriodFrom < dayEnd && x.PeriodTo > day).ToListAsync(ct);

                // 删除该日该产线的分钟/小时/日行后重建。
                await db.OeeRollups.Where(x => x.LineId == line.Id && x.Granularity == OeeRollup.Minute && x.BucketTs >= day && x.BucketTs < dayEnd).ExecuteDeleteAsync(ct);
                await db.OeeRollups.Where(x => x.LineId == line.Id && x.Granularity == OeeRollup.Hour && x.BucketTs >= day && x.BucketTs < dayEnd).ExecuteDeleteAsync(ct);
                await db.OeeRollups.Where(x => x.LineId == line.Id && x.Granularity == OeeRollup.Day && x.BucketTs == day).ExecuteDeleteAsync(ct);

                var minutes = new List<OeeRollup>();
                if (dayEnd > minuteCut)
                {
                    for (var m = OeeService.FloorMinute(Math.Max(day, start)); m < dayEnd; m += OeeMath.MinuteMs)
                    {
                        var me = Math.Min(m + OeeMath.MinuteMs, dayEnd);
                        var row = BuildBucket(line, m, me, OeeRollup.Minute, intervals, corrections, lineShifts, oee);
                        if (HasData(row)) minutes.Add(row);
                    }
                    db.OeeRollups.AddRange(minutes);
                }

                var hours = new List<OeeRollup>();
                if (dayEnd > hourCut)
                {
                    for (var h = OeeService.FloorHour(Math.Max(day, start)); h < dayEnd; h += OeeMath.HourMs)
                    {
                        var he = Math.Min(h + OeeMath.HourMs, dayEnd);
                        var src = minutes.Where(x => x.BucketTs >= h && x.BucketTs < he).ToList();
                        OeeRollup row;
                        if (src.Count > 0) row = Sum(line, h, OeeRollup.Hour, src);
                        else row = BuildBucket(line, h, he, OeeRollup.Hour, intervals, corrections, lineShifts, oee);
                        if (HasData(row)) hours.Add(row);
                    }
                    db.OeeRollups.AddRange(hours);
                }

                var daySrc = hours.Count > 0 ? hours : minutes;
                var dayRow = daySrc.Count > 0 ? Sum(line, day, OeeRollup.Day, daySrc)
                    : BuildBucket(line, day, dayEnd, OeeRollup.Day, intervals, corrections, lineShifts, oee);
                if (HasData(dayRow)) { db.OeeRollups.Add(dayRow); written++; }
                written += minutes.Count + hours.Count;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            db.Attach(state);
        }

        await db.OeeRollups.Where(x => x.Granularity == OeeRollup.Minute && x.BucketTs < minuteCut).ExecuteDeleteAsync(ct);
        await db.OeeRollups.Where(x => x.Granularity == OeeRollup.Hour && x.BucketTs < hourCut).ExecuteDeleteAsync(ct);
        await db.OeeRollups.Where(x => x.Granularity == OeeRollup.Day && x.BucketTs < dayCut).ExecuteDeleteAsync(ct);
        state.WatermarkTs = closedMin; state.LastRunTs = now; state.RebuildFromTs = null; state.LastError = null;
        await db.SaveChangesAsync(ct);
        return written;
    }

    private static bool HasData(OeeRollup r) =>
        r.PlannedMs > 0 || r.RunMs > 0 || r.DownMs > 0 || r.GoodCount != 0 || r.RejectCount != 0 || r.ScrapCount != 0;

    private static OeeRollup Sum(ProductionLine line, long bucket, int gran, List<OeeRollup> src)
    {
        double idealW = 0, idealT = 0;
        var row = new OeeRollup { Granularity = gran, BucketTs = bucket, LineId = line.Id, ProductId = line.ProductId };
        foreach (var s in src)
        {
            row.PlannedMs += s.PlannedMs; row.RunMs += s.RunMs; row.DownMs += s.DownMs; row.PlannedDownMs += s.PlannedDownMs;
            row.FaultMs += s.FaultMs; row.SetupMs += s.SetupMs;
            row.GoodCount += s.GoodCount; row.RejectCount += s.RejectCount; row.ScrapCount += s.ScrapCount;
            row.TargetCount += s.TargetCount;
            if (s.IdealCycleSec > 0 && s.RunMs > 0) { idealW += s.IdealCycleSec * s.RunMs; idealT += s.RunMs; }
        }
        row.IdealCycleSec = idealT > 0 ? idealW / idealT : src.FirstOrDefault()?.IdealCycleSec ?? 0;
        return row;
    }

    private static OeeRollup BuildBucket(ProductionLine line, long from, long to, int gran,
        List<OeeInterval> intervals, List<ProductionCountCorrection> corrections, List<OeeShiftWindow> shifts, OeeService oee)
    {
        var plannedSegs = OeeMath.PlannedSegments(from, to, shifts);
        var th = new OeeThresholds();
        var counts = corrections.Select(c => new OeeCountSlice(c.PeriodFrom, c.PeriodTo, c.GoodDelta, c.RejectDelta, c.ScrapDelta,
            OeeMath.EffectiveIdealCycleSec(line.RatedUnitsPerHour, line.IdealCycleSec), line.TargetUnitsPerHour ?? 0, c.ProductId)).ToList();
        var b = OeeMath.ComputeBucket(from, to, "", intervals, counts, plannedSegs, th, line.Id, line.Name);
        return new OeeRollup
        {
            Granularity = gran, BucketTs = from, LineId = line.Id, ProductId = line.ProductId,
            PlannedMs = b.PlannedMs, RunMs = b.RunMs, DownMs = b.DownMs, PlannedDownMs = b.PlannedDownMs,
            FaultMs = b.FaultMs, SetupMs = b.SetupMs,
            GoodCount = b.Good, RejectCount = b.Reject, ScrapCount = b.Scrap,
            IdealCycleSec = b.IdealCycleSec ?? 0, TargetCount = b.Target
        };
    }

    public async Task RequestRebuildAsync(long fromTs, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var state = await db.OeeRollupStates.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (state is null) { state = new OeeRollupState { Id = 1 }; db.OeeRollupStates.Add(state); }
        state.RebuildFromTs = state.RebuildFromTs is long r ? Math.Min(r, fromTs) : fromTs;
        await db.SaveChangesAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.OeeRollupEnabled) return;
        await Task.Delay(TimeSpan.FromSeconds(35), stoppingToken).ContinueWith(_ => { });
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _settings.OeeRollupIntervalSec)));
        do
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (services.GetService<INodeRole>() is { IsActive: false }) continue;
            try { await RunOnceAsync(ScadaTime.NowMs(), stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "OEE 预聚合失败");
                try
                {
                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var state = await db.OeeRollupStates.FirstOrDefaultAsync(x => x.Id == 1, stoppingToken);
                    if (state is not null) { state.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message; await db.SaveChangesAsync(stoppingToken); }
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested) { }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => !t.IsCanceled && t.Result));
    }
}
