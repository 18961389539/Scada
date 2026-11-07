using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Spc;

// SPC 采样：按控制图的采样周期从实时缓存取值，凑满子组后计算均值/极差、控制限、判异规则，
// 并把持续失控按“每图每规则一条活动事件”的方式联动现有报警通知。
public sealed class SpcMonitorService(
    IDbContextFactory<ConfigDbContext> configFactory,
    IDbContextFactory<RuntimeDbContext> runtimeFactory,
    IRealtimeCache cache,
    IAlarmNotifier notifier,
    INodeRole role,
    ILogger<SpcMonitorService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, ChartState> _states = new();
    private IReadOnlyList<SpcChart> _charts = [];
    private long _chartsLoadedTs = long.MinValue;
    private long _lastRecoverTs = long.MinValue;

    private sealed class ChartState
    {
        public long NextDueTs { get; set; }
        public int Revision { get; set; }
        public List<double> Pending { get; } = [];
    }

    public async Task SampleOnceAsync(long now, CancellationToken ct = default, bool forceReload = false)
    {
        await _gate.WaitAsync(ct);
        try { await SampleCoreAsync(now, forceReload, ct); }
        finally { _gate.Release(); }
    }

    private async Task SampleCoreAsync(long now, bool forceReload, CancellationToken ct)
    {
        // 配置读取降到每 10 秒一次：采样周期最小 5 秒，没必要每秒都打开配置库；
        // 控制图删改后最多 10 秒生效，期间用缓存配置继续采样不会丢点。
        if (forceReload || _chartsLoadedTs == long.MinValue || now - _chartsLoadedTs >= 10_000)
        {
            await using var config = await configFactory.CreateDbContextAsync(ct);
            _charts = await config.SpcCharts.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);
            _chartsLoadedTs = now;
            var activeIds = _charts.Select(x => x.Id).ToHashSet();
            foreach (var stale in _states.Keys.Where(id => !activeIds.Contains(id)).ToArray()) _states.Remove(stale);
        }
        if (_charts.Count == 0 || _lastRecoverTs == long.MinValue || now - _lastRecoverTs >= 10_000)
        {
            _lastRecoverTs = now;
            await RecoverStaleAsync(now, ct);
        }

        foreach (var chart in _charts)
        {
            ct.ThrowIfCancellationRequested();
            var state = _states.TryGetValue(chart.Id, out var existing) ? existing : _states[chart.Id] = new ChartState();
            // 采样代次变化意味着测点/子组口径已变：丢弃旧口径的待凑样本，避免新旧值混进同一子组。
            if (state.Revision != chart.Revision)
            {
                state.Pending.Clear();
                state.Revision = chart.Revision;
                state.NextDueTs = 0;
            }
            if (now < state.NextDueTs) continue;
            state.NextDueTs = now + chart.SamplingIntervalSec * 1000L;

            var value = cache.Get(chart.TagId);
            if (value is not { Quality: TagQuality.Good } || !double.IsFinite(value.Value)) continue;

            var size = chart.ChartType == SpcChartType.Individuals ? 1 : Math.Clamp(chart.SubgroupSize, 2, 10);
            state.Pending.Add(value.Value);
            if (state.Pending.Count < size) continue;

            var values = state.Pending.Take(size).ToArray();
            state.Pending.RemoveRange(0, size);
            await AppendSampleAsync(chart, values, now, ct);
        }
    }

    private async Task AppendSampleAsync(SpcChart chart, double[] values, long now, CancellationToken ct)
    {
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var recent = await runtime.SpcSamples.AsNoTracking()
            .Where(x => x.ChartId == chart.Id && x.Revision == chart.Revision)
            .OrderByDescending(x => x.Ts).Take(40).ToListAsync(ct);
        recent.Reverse();

        var size = chart.ChartType == SpcChartType.Individuals ? 1 : Math.Clamp(chart.SubgroupSize, 2, 10);
        var mean = values.Average();
        var range = chart.ChartType == SpcChartType.Individuals
            ? recent.Count > 0 ? Math.Abs(mean - recent[^1].Mean) : 0
            : values.Max() - values.Min();

        var historicalMeans = recent.Select(x => x.Mean).ToArray();
        var historicalRanges = recent.Select(x => x.Range).ToArray();
        var means = historicalMeans.Append(mean).ToArray();
        var ranges = historicalRanges.Append(range).ToArray();

        // 自动控制限按“最初 N 个样本”建立；不能用最近 40 个截断，否则基线 >40 时永远建立不起来。
        double[] limitMeans = historicalMeans, limitRanges = historicalRanges;
        if (chart.AutoLimits && chart.LimitsEstablishedTs is null)
        {
            var baseline = await runtime.SpcSamples.AsNoTracking()
                .Where(x => x.ChartId == chart.Id && x.Revision == chart.Revision)
                .OrderBy(x => x.Ts).Take(chart.BaselineSubgroups).ToListAsync(ct);
            limitMeans = baseline.Select(x => x.Mean).ToArray();
            limitRanges = baseline.Select(x => x.Range).ToArray();
        }

        var limits = await ResolveLimitsAsync(chart, limitMeans, limitRanges, size, now, ct);
        var rules = limits is null ? Array.Empty<SpcViolationRule>() : SpcMath.Evaluate(means, ranges, limits, chart);
        var (cp, cpk) = limits is null
            ? (null, null)
            : SpcMath.Capability(limits.CenterLine, limits.SigmaWithin, chart.SpecUpperLimit, chart.SpecLowerLimit);

        var sample = new SpcSample
        {
            ChartId = chart.Id, Revision = chart.Revision, Ts = now, Mean = mean, Range = range,
            SubgroupSize = size, ValuesJson = JsonSerializer.Serialize(values.Select(v => Math.Round(v, 6))),
            CenterLine = limits?.CenterLine, UpperControlLimit = limits?.UpperControlLimit,
            LowerControlLimit = limits?.LowerControlLimit, RangeCenter = limits?.RangeCenter,
            RangeUpperControlLimit = limits?.RangeUpperControlLimit, RangeLowerControlLimit = limits?.RangeLowerControlLimit,
            RuleCodes = string.Join(",", rules.Select(SpcRuleText.Code)), Cp = cp, Cpk = cpk
        };
        runtime.SpcSamples.Add(sample);
        await SaveWithRetryAsync(runtime, ct);

        await SyncViolationsAsync(runtime, chart, sample, rules, now, ct);
    }

    private async Task<SpcLimits?> ResolveLimitsAsync(SpcChart chart,
        double[] historicalMeans, double[] historicalRanges, int size, long now, CancellationToken ct)
    {
        if (!chart.AutoLimits &&
            chart.CenterLine is double manualCenter && chart.UpperControlLimit is double manualUcl && chart.LowerControlLimit is double manualLcl)
        {
            var sigma = (manualUcl - manualLcl) / 6;
            if (sigma <= 0 || !double.IsFinite(sigma)) return null;
            var rangeLimits = SpcMath.ComputeLimits(historicalMeans, historicalRanges, chart.ChartType, size);
            return new SpcLimits(manualCenter, manualUcl, manualLcl,
                rangeLimits?.RangeCenter, rangeLimits?.RangeUpperControlLimit, rangeLimits?.RangeLowerControlLimit, sigma, false);
        }

        if (chart.LimitsEstablishedTs is not null &&
            chart.CenterLine is double frozenCenter && chart.UpperControlLimit is double frozenUcl && chart.LowerControlLimit is double frozenLcl)
        {
            var sigma = chart.RangeCenter is double rangeCenter && rangeCenter > 0
                ? rangeCenter / SpcMath.D2(size)
                : (frozenUcl - frozenLcl) / 6;
            if (sigma <= 0 || !double.IsFinite(sigma)) return null;
            return new SpcLimits(frozenCenter, frozenUcl, frozenLcl,
                chart.RangeCenter, chart.RangeUpperControlLimit, chart.RangeLowerControlLimit, sigma, false);
        }

        // 自动控制限：基线子组足够时冻结并写入配置，之后不再随新样本漂移。
        var baselineMeans = historicalMeans.Take(chart.BaselineSubgroups).ToArray();
        var baselineRanges = historicalRanges.Take(chart.BaselineSubgroups).ToArray();
        if (baselineMeans.Length >= chart.BaselineSubgroups)
        {
            var computed = SpcMath.ComputeLimits(baselineMeans, baselineRanges, chart.ChartType, size);
            if (computed is null) return null;
            chart.CenterLine = computed.CenterLine; chart.UpperControlLimit = computed.UpperControlLimit;
            chart.LowerControlLimit = computed.LowerControlLimit; chart.RangeCenter = computed.RangeCenter;
            chart.RangeUpperControlLimit = computed.RangeUpperControlLimit; chart.RangeLowerControlLimit = computed.RangeLowerControlLimit;
            chart.LimitsEstablishedTs = now;
            await using var config = await configFactory.CreateDbContextAsync(ct);
            await config.SpcCharts.Where(x => x.Id == chart.Id && x.Revision == chart.Revision)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CenterLine, computed.CenterLine)
                    .SetProperty(x => x.UpperControlLimit, computed.UpperControlLimit)
                    .SetProperty(x => x.LowerControlLimit, computed.LowerControlLimit)
                    .SetProperty(x => x.RangeCenter, computed.RangeCenter)
                    .SetProperty(x => x.RangeUpperControlLimit, computed.RangeUpperControlLimit)
                    .SetProperty(x => x.RangeLowerControlLimit, computed.RangeLowerControlLimit)
                    .SetProperty(x => x.LimitsEstablishedTs, now), ct);
            return computed;
        }

        var provisional = SpcMath.ComputeLimits(historicalMeans, historicalRanges, chart.ChartType, size);
        return provisional is null ? null : provisional with { Provisional = true };
    }

    private async Task SyncViolationsAsync(RuntimeDbContext runtime, SpcChart chart, SpcSample sample,
        IReadOnlyList<SpcViolationRule> currentRules, long now, CancellationToken ct)
    {
        var active = await runtime.SpcViolations.Where(x => x.ChartId == chart.Id && x.RecoveredTs == null).ToListAsync(ct);
        var current = currentRules.ToHashSet();
        var recover = new List<SpcViolation>();
        foreach (var violation in active)
        {
            if (current.Contains(violation.Rule)) continue;
            violation.RecoveredTs = now;
            recover.Add(violation);
            if (violation.AlarmEventId is int alarmId)
            {
                var alarm = await runtime.AlarmEvents.FirstOrDefaultAsync(x => x.Id == alarmId, ct);
                if (alarm is not null && alarm.EndTs is null)
                {
                    alarm.EndTs = now; alarm.EndReason = "SPC 恢复";
                    runtime.AlarmActivities.Add(new AlarmActivity
                    {
                        AlarmEventId = alarm.Id, Ts = now, User = "spc-monitor", Action = "recover",
                        Note = $"SPC 恢复：{chart.Name} · {SpcRuleText.Label(violation.Rule)}"
                    });
                }
            }
        }

        var raise = new List<(SpcViolation Violation, AlarmEvent Alarm)>();
        foreach (var rule in currentRules)
        {
            if (active.Any(x => x.Rule == rule)) continue;
            var message = $"SPC 失控：{chart.Name} · {SpcRuleText.Label(rule)}";
            var messageEn = $"SPC out of control: {chart.Name} · {SpcRuleText.LabelEn(rule)}";
            var alarm = new AlarmEvent
            {
                TagId = chart.TagId,
                Level = rule == SpcViolationRule.Beyond3Sigma ? AlarmLevel.Critical : AlarmLevel.Warning,
                Category = AlarmCategory.Process, Consequence = AlarmConsequence.Major,
                Value = sample.Mean, StartTs = sample.Ts, Message = Truncate(message, 500), MessageEn = Truncate(messageEn, 500)
            };
            runtime.AlarmEvents.Add(alarm);
            await SaveWithRetryAsync(runtime, ct);
            runtime.AlarmActivities.Add(new AlarmActivity
            {
                AlarmEventId = alarm.Id, Ts = now, User = "spc-monitor", Action = "raise", Note = message
            });
            var violation = new SpcViolation
            {
                ChartId = chart.Id, Revision = chart.Revision, SampleId = sample.Id, Rule = rule,
                Ts = sample.Ts, AlarmEventId = alarm.Id, Message = Truncate(message, 300)
            };
            runtime.SpcViolations.Add(violation);
            raise.Add((violation, alarm));
        }
        if (recover.Count > 0 || raise.Count > 0) await SaveWithRetryAsync(runtime, ct);

        foreach (var violation in recover)
            if (violation.AlarmEventId is int alarmId)
                await NotifySafeAsync(violation.ChartId, alarmId, "recover", ct);
        foreach (var (_, alarm) in raise)
            await NotifySafeAsync(chart.Id, alarm.Id, "raise", ct);
    }

    // 图被停用/删除或配置修订变化后，把旧的活动违规和报警恢复，避免永久挂起。
    private async Task RecoverStaleAsync(long now, CancellationToken ct)
    {
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        await using var config = await configFactory.CreateDbContextAsync(ct);
        var charts = await config.SpcCharts.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var knownIds = charts.Keys.ToArray();

        var active = await runtime.SpcViolations.Where(x => x.RecoveredTs == null).ToListAsync(ct);
        var recovered = new List<int>();
        var changed = false;
        foreach (var violation in active)
        {
            var chart = charts.GetValueOrDefault(violation.ChartId);
            if (chart is { Enabled: true } && chart.Revision == violation.Revision) continue;
            violation.RecoveredTs = now;
            changed = true;
            if (violation.AlarmEventId is int alarmId)
            {
                recovered.Add(alarmId);
                var alarm = await runtime.AlarmEvents.FirstOrDefaultAsync(x => x.Id == alarmId, ct);
                if (alarm is not null && alarm.EndTs is null)
                {
                    alarm.EndTs = now; alarm.EndReason = "SPC 控制图停用或删除";
                    runtime.AlarmActivities.Add(new AlarmActivity
                    {
                        AlarmEventId = alarm.Id, Ts = now, User = "spc-monitor", Action = "recover",
                        Note = "SPC 控制图停用、删除或采样口径已变化"
                    });
                }
            }
        }
        if (changed) await SaveWithRetryAsync(runtime, ct);

        // 清理已删除控制图（包括级联删除测点后消失的图）留下的运行库数据。
        if (knownIds.Length == 0)
        {
            await runtime.SpcSamples.ExecuteDeleteAsync(ct);
            await runtime.SpcViolations.ExecuteDeleteAsync(ct);
        }
        else
        {
            await runtime.SpcSamples.Where(x => !knownIds.Contains(x.ChartId)).ExecuteDeleteAsync(ct);
            await runtime.SpcViolations.Where(x => !knownIds.Contains(x.ChartId)).ExecuteDeleteAsync(ct);
        }

        foreach (var alarmId in recovered) await NotifySafeAsync(0, alarmId, "recover", ct);
    }

    // SQLite 写锁是短时的，遇到 busy 用退避重试，避免偶尔一次争用就丢掉整个子组。
    private static async Task SaveWithRetryAsync(DbContext db, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await db.SaveChangesAsync(ct); return; }
            catch (DbUpdateException ex) when (attempt < 5 && IsBusy(ex))
            {
                await Task.Delay(200 * attempt, ct);
            }
        }
    }

    private static bool IsBusy(DbUpdateException ex) =>
        ex.InnerException is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6;

    private async Task NotifySafeAsync(int chartId, int alarmId, string action, CancellationToken ct)
    {
        try
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var alarm = await runtime.AlarmEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == alarmId, ct);
            if (alarm is not null) await notifier.PublishAlarmAsync(alarm, action, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SPC 报警推送失败：图 {ChartId} / 报警 {AlarmId}", chartId, alarmId);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ContinueWith(_ => { });
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (role.IsActive)
            {
                try { await SampleOnceAsync(ScadaTime.NowMs(), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "SPC 采样失败"); }
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
