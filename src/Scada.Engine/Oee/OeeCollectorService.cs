using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
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
    IDbContextFactory<RuntimeDbContext> runtimeFactory,
    IRealtimeCache cache,
    IOptions<ScadaSettings> options,
    ILogger<OeeCollectorService> logger) : BackgroundService
{
    private readonly ScadaSettings _settings = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // 状态映射配置是静态配置，坏了不会自己好；每条产线只报一次，避免采样循环把日志刷爆。
    private readonly HashSet<int> _reportedStateMapErrors = [];
    // 上一拍因状态测点质量无效被冻结的产线。恢复后的第一拍仍跳过时间累加（该段 [prevTs, now) 发生在质量恢复之前）。
    private readonly HashSet<int> _stateQualityFrozen = [];
    // 已为当前冻结段打过日志的产线，质量恢复后清除，避免采样循环刷屏。
    private readonly HashSet<int> _reportedQualityFreezes = [];
    private readonly ConcurrentDictionary<int, string> _freezeReasons = new();

    public string? GetFreezeReason(int lineId) => _freezeReasons.TryGetValue(lineId, out var reason) ? reason : null;


    public async Task SampleOnceAsync(long now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await SampleCoreAsync(now, ct); }
        finally { _gate.Release(); }
    }

    private async Task SampleCoreAsync(long now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var lines = await db.ProductionLines.Where(x => x.Enabled).ToListAsync(ct);
        if (lines.Count == 0) return;
        var products = await db.ProductionProducts.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var openEvents = await runtime.DowntimeEvents.Where(x => x.EndTs == null && x.Source == "tag").ToListAsync(ct);
        var samples = await runtime.OeeLineSamples.ToDictionaryAsync(x => x.LineId, ct);
        var openByLine = openEvents.GroupBy(x => x.LineId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.StartTs).First());

        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            var state = LineState.Running;
            var stateQualityValid = true;
            if (line.StateTagId is int stId)
            {
                var v = cache.Get(stId);
                stateQualityValid = v is { Quality: TagQuality.Good, Simulated: false } && double.IsFinite(v.Value);
                if (stateQualityValid)
                {
                    state = OeeService.MapStateDetailed(line.StateMapJson, v!.Value, out var mapError);
                    if (mapError is not null && _reportedStateMapErrors.Add(line.Id))
                        logger.LogWarning("产线 {LineId}「{LineName}」的状态映射配置无法解析，已按 0=停止/非 0=运行 兜底：{Error}", line.Id, line.Name, mapError);
                    if (state == LineState.Unknown) state = LineState.Running;
                }
            }

            double? good = Read(line.GoodCountTagId), reject = Read(line.RejectCountTagId), total = Read(line.TotalCountTagId);
            samples.TryGetValue(line.Id, out var prev);
            var prevTs = prev?.Ts ?? now;
            var elapsed = Math.Max(0, now - prevTs);

            // 配置了状态测点且当前质量无效时：保持上一拍状态，不开关停机事件，也不把该段记入可用/停机时间。
            var stateFrozen = line.StateTagId is not null && !stateQualityValid;
            var prevState = prev?.State ?? LineState.Running;
            if (stateFrozen)
                state = prev?.State ?? LineState.Unknown;
            else if (state != prevState || (state != LineState.Running && !openByLine.ContainsKey(line.Id)))
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
                    runtime.DowntimeEvents.Add(ev);
                    openByLine[line.Id] = ev;
                }
            }

            // 计数增量（处理回绕：新值 < 旧值时按新值起算）。坏质量拍不冲掉上次有效计数。
            double dGood = 0, dReject = 0, dScrap = 0;
            var countFrozen = (line.GoodCountTagId is not null && good is null)
                || (line.RejectCountTagId is not null && reject is null)
                || (line.TotalCountTagId is not null && total is null);
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

            // 本拍状态质量无效、尚未有过有效状态，或上一拍刚从冻结恢复：该段时间不记入 RunMs/停机/计划时间。
            var skipTime = stateFrozen || _stateQualityFrozen.Contains(line.Id) || (prev is not null && prev.State == LineState.Unknown);

            if (elapsed > 0 || dGood > 0 || dReject > 0 || dScrap > 0)
            {
                if (skipTime)
                {
                    // 时间跳过；若本拍计数质量有效，产量增量仍记入当前分钟桶。
                    if (dGood > 0 || dReject > 0 || dScrap > 0)
                    {
                        var bucket = OeeService.FloorMinute(now > prevTs ? now - 1 : now);
                        var row = await runtime.OeeRollups.FirstOrDefaultAsync(x =>
                            x.Granularity == OeeRollup.Minute && x.BucketTs == bucket && x.LineId == line.Id && x.ProductId == line.ProductId, ct);
                        if (row is null)
                        {
                            row = new OeeRollup
                            {
                                Granularity = OeeRollup.Minute, BucketTs = bucket, LineId = line.Id, ProductId = line.ProductId,
                                IdealCycleSec = ideal
                            };
                            runtime.OeeRollups.Add(row);
                        }
                        row.GoodCount += dGood;
                        row.RejectCount += dReject;
                        row.ScrapCount += dScrap;
                        if (ideal > 0) row.IdealCycleSec = ideal;
                    }
                }
                else
                {
                    // 把 [prevTs, now) 按分钟切开写入预聚合。
                    var cursor = prevTs;
                    while (cursor < now)
                    {
                        var bucket = OeeService.FloorMinute(cursor);
                        var next = Math.Min(now, bucket + OeeMath.MinuteMs);
                        var ms = next - cursor;
                        var frac = elapsed > 0 ? ms / (double)elapsed : 1;
                        var row = await runtime.OeeRollups.FirstOrDefaultAsync(x =>
                            x.Granularity == OeeRollup.Minute && x.BucketTs == bucket && x.LineId == line.Id && x.ProductId == line.ProductId, ct);
                        if (row is null)
                        {
                            row = new OeeRollup
                            {
                                Granularity = OeeRollup.Minute, BucketTs = bucket, LineId = line.Id, ProductId = line.ProductId,
                                IdealCycleSec = ideal
                            };
                            runtime.OeeRollups.Add(row);
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
            }

            if (prev is null)
            {
                prev = new OeeLineSample { LineId = line.Id, State = LineState.Unknown };
                runtime.OeeLineSamples.Add(prev);
                samples[line.Id] = prev;
            }
            prev.Ts = now;
            if (!stateFrozen) prev.State = state;
            if (good is not null) prev.GoodCount = good;
            if (reject is not null) prev.RejectCount = reject;
            if (total is not null) prev.TotalCount = total;
            prev.ProductId = line.ProductId;

            if (stateFrozen) _stateQualityFrozen.Add(line.Id);
            else _stateQualityFrozen.Remove(line.Id);

            if (stateFrozen || countFrozen)
            {
                var why = DescribeFreeze(line, stateFrozen, countFrozen);
                _freezeReasons[line.Id] = why;
                if (_reportedQualityFreezes.Add(line.Id))
                    logger.LogWarning("产线 {LineId}「{LineName}」的{What}因{Why}被冻结，将保持上一拍有效值直到恢复",
                        line.Id, line.Name, FreezeLabel(stateFrozen, countFrozen), why);
            }
            else
            {
                _reportedQualityFreezes.Remove(line.Id);
                _freezeReasons.TryRemove(line.Id, out _);
            }
        }
        await db.SaveChangesAsync(ct);
        await runtime.SaveChangesAsync(ct);

        double? Read(int? tagId) => tagId is int id && cache.Get(id) is { Quality: TagQuality.Good, Simulated: false } v && double.IsFinite(v.Value) ? v.Value : null;
        static double Delta(double? prev, double? cur)
        {
            if (prev is null || cur is null) return 0;
            return cur.Value >= prev.Value ? cur.Value - prev.Value : cur.Value;
        }
        static string FreezeLabel(bool stateFrozen, bool countFrozen) => (stateFrozen, countFrozen) switch
        {
            (true, true) => "状态和计数",
            (true, false) => "状态",
            _ => "计数"
        };
        string DescribeFreeze(ProductionLine line, bool stateFrozen, bool countFrozen)
        {
            var parts = new List<string>();
            void Check(int? id, string label)
            {
                if (id is not int tagId) return;
                var value = cache.Get(tagId);
                if (value is { Simulated: true }) parts.Add($"{label}为仿真值");
                else if (value is null || value.Quality != TagQuality.Good || !double.IsFinite(value.Value))
                    parts.Add(value is null ? $"{label}无有效值" : $"{label}质量{value.Quality}");
            }
            if (stateFrozen) Check(line.StateTagId, "状态测点");
            if (countFrozen)
            {
                Check(line.GoodCountTagId, "良品计数");
                Check(line.RejectCountTagId, "次品计数");
                Check(line.TotalCountTagId, "总计数");
            }
            return parts.Count > 0 ? string.Join("，", parts) : "测点质量无效或为仿真值";
        }
    }

    // 把报警导入为待确认停机候选（同报警不重复）。
    public async Task<int> ImportAlarmCandidatesAsync(long from, long to, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var minLevel = (AlarmLevel)Math.Clamp(_settings.OeeAlarmDowntimeMinLevel, 0, 3);
        var lines = await db.ProductionLines.AsNoTracking().Where(x => x.Enabled && x.StateTagId != null).ToListAsync(ct);
        var tagToLine = lines.Where(l => l.StateTagId is not null).ToDictionary(l => l.StateTagId!.Value, l => l.Id);
        if (tagToLine.Count == 0) return 0;
        var tagIds = tagToLine.Keys.ToList();
        var existing = (await runtime.DowntimeEvents.AsNoTracking().Where(x => x.Source == "alarm" && x.AlarmEventId != null && x.StartTs >= from && x.StartTs < to)
            .Select(x => x.AlarmEventId!.Value).ToListAsync(ct)).ToHashSet();
        var alarms = await runtime.AlarmEvents.AsNoTracking()
            .Where(x => x.TagId != null && tagIds.Contains(x.TagId.Value) && x.StartTs >= from && x.StartTs < to && x.Level >= minLevel)
            .OrderBy(x => x.StartTs).Take(5000).ToListAsync(ct);
        var n = 0;
        foreach (var a in alarms)
        {
            if (a.TagId is not int t || !tagToLine.TryGetValue(t, out var lineId) || existing.Contains(a.Id)) continue;
            runtime.DowntimeEvents.Add(new DowntimeEvent
            {
                LineId = lineId, StartTs = a.StartTs, EndTs = a.EndTs, State = LineState.Fault, Source = "alarm",
                AlarmEventId = a.Id, Note = a.Message, Confirmed = false, CreatedBy = "oee-alarm-import", CreatedTs = ScadaTime.NowMs()
            });
            n++;
        }
        if (n > 0) await runtime.SaveChangesAsync(ct);
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
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
