using System.Globalization;
using System.Text.Json;
using Scada.Core.Entities;

namespace Scada.Core.Calculations;

public readonly record struct JobSample(double Value, bool Good, long Ts);
public sealed record TouPeriod(string Band, string Start, string End);
public sealed record PendingScriptWrite(Guid CommandId, double Value);

// 计算任务的持久化运行状态（序列化为 CalcJobState.StateJson）。
public sealed class JobRuntimeState
{
    public Dictionary<string, double> Values { get; set; } = new();
    public double? LastRaw { get; set; }
    public long? LastTs { get; set; }
    public double? LastValue { get; set; }
    public double? LastGood { get; set; }
    public long? LastGoodTs { get; set; }
    public bool? LastRunning { get; set; }
    public string PeriodKey { get; set; } = "";
    public long PeriodStartTs { get; set; }
    public double MaintenanceBase { get; set; }
    public Dictionary<int, double> LastWrites { get; set; } = new();
    public Dictionary<int, PendingScriptWrite> PendingWrites { get; set; } = new();
    public string? LastEvent { get; set; }

    public double Get(string key) => Values.GetValueOrDefault(key);
    public void Add(string key, double delta) => Values[key] = Values.GetValueOrDefault(key) + delta;
}

public sealed record PeriodSnapshot(string PeriodKey, long PeriodStartTs, Dictionary<string, double> Values);

public static class CalcJobMath
{
    public static readonly string[] Bands = ["sharp", "peak", "flat", "valley"];

    // 每类任务的固定输出（脚本的输出由 @名称 决定）。
    public static IReadOnlyList<string> Outputs(CalcJobKind kind) => kind switch
    {
        CalcJobKind.Accumulator => ["total", "period"],
        CalcJobKind.RunHours => ["hours", "periodHours", "starts", "periodStarts", "sinceMaintenance", "maintenanceDue"],
        CalcJobKind.Energy => ["total", "period", "period_sharp", "period_peak", "period_flat", "period_valley"],
        _ => []
    };

    public static bool IsPeriodKey(string key) => key.StartsWith("period", StringComparison.Ordinal);

    // 有效输入值：好且未过期则取当前值；否则按坏质量策略处理。返回 null 表示该时刻值未知。
    public static double? Effective(CalcJob job, JobRuntimeState state, JobSample? sample, long now)
    {
        var maxGap = Math.Max(1, job.MaxGapSec) * 1000L;
        if (sample is { Good: true } s && double.IsFinite(s.Value) && now - s.Ts <= maxGap)
        {
            state.LastGood = s.Value; state.LastGoodTs = now;
            return s.Value;
        }
        return job.BadQuality switch
        {
            BadQualityPolicy.Hold => state.LastGoodTs is long t && now - t <= maxGap ? state.LastGood : null,
            BadQualityPolicy.Zero => 0,
            _ => null
        };
    }

    // 累计器：脉冲计数（处理满量程回绕与复位）或瞬时流量梯形积分。返回本次增量。
    public static double Accumulate(CalcJob job, JobRuntimeState state, JobSample? sample, long now)
    {
        double increment = 0;
        if (job.Mode == AccumulatorMode.Pulse)
        {
            // 计数器在设备里持续累加，坏质量期间不更新基准，恢复后的差值自然补上这段时间。
            if (sample is not { Good: true } s || !double.IsFinite(s.Value)) return 0;
            var raw = s.Value;
            if (state.LastRaw is double last)
            {
                var delta = raw - last;
                if (delta < 0)
                {
                    if (job.CounterMax is double max && max > 0 && last > max / 2 && raw < max / 2)
                    { delta = max - last + raw + 1; state.LastEvent = $"计数器回绕 {last}→{raw}"; }
                    else
                    { delta = Math.Max(0, raw); state.LastEvent = $"计数器复位 {last}→{raw}"; }
                }
                if (job.MaxDelta is double limit && delta > limit) { state.LastEvent = $"增量 {delta} 超过上限 {limit}，已丢弃"; delta = 0; }
                increment = delta * job.Factor;
            }
            state.LastRaw = raw;
        }
        else increment = Integrate(job, state, sample, now, out _);
        state.Add("total", increment); state.Add("period", increment);
        return increment;
    }

    // 梯形积分（流量/功率）：两次采样都有效且间隔不超过 MaxGapSec 才计入；负值按 0 处理。
    public static double Integrate(CalcJob job, JobRuntimeState state, JobSample? sample, long now, out long midpoint)
    {
        midpoint = now;
        var value = Effective(job, state, sample, now);
        double increment = 0;
        if (value is double v && state.LastValue is double previous && state.LastTs is long lastTs)
        {
            var dt = (now - lastTs) / 1000.0;
            if (dt > 0 && dt <= Math.Max(1, job.MaxGapSec))
            {
                increment = Math.Max(0, (previous + v) / 2 * dt * job.Factor);
                midpoint = lastTs + (now - lastTs) / 2;
            }
        }
        state.LastValue = value; state.LastTs = now;
        return increment;
    }

    public static double Energy(CalcJob job, JobRuntimeState state, JobSample? sample, long now, IReadOnlyList<TouPeriod> tou, TimeZoneInfo zone)
    {
        var increment = Integrate(job, state, sample, now, out var midpoint);
        if (increment <= 0) return 0;
        state.Add("total", increment); state.Add("period", increment);
        state.Add("period_" + Band(tou, TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(midpoint), zone).TimeOfDay), increment);
        return increment;
    }

    // 运行小时与启停次数：按上一时刻的运行状态累计区间时长；状态未知（坏质量且策略为 Skip）时不累计也不计启停。
    public static void RunHours(CalcJob job, JobRuntimeState state, JobSample? sample, long now)
    {
        var value = Effective(job, state, sample, now);
        bool? running = value is double v ? v > job.RunThreshold : null;
        if (state.LastRunning == true && state.LastTs is long lastTs)
        {
            var dt = (now - lastTs) / 1000.0;
            if (dt > 0 && dt <= Math.Max(1, job.MaxGapSec)) { state.Add("hours", dt / 3600); state.Add("periodHours", dt / 3600); }
        }
        if (running == true && state.LastRunning == false) { state.Add("starts", 1); state.Add("periodStarts", 1); }
        if (running is not null) state.LastRunning = running;
        else if (job.BadQuality == BadQualityPolicy.Skip) state.LastRunning = null;
        state.LastTs = now;
        var since = state.Get("hours") - state.MaintenanceBase;
        state.Values["sinceMaintenance"] = since;
        state.Values["maintenanceDue"] = job.MaintenanceHours is double limit && limit > 0 && since >= limit ? 1 : 0;
    }

    public static string Band(IReadOnlyList<TouPeriod> periods, TimeSpan local)
    {
        foreach (var period in periods)
        {
            var start = ParseTime(period.Start); var end = ParseTime(period.End);
            var inside = start <= end ? local >= start && local < end : local >= start || local < end;
            if (inside) return period.Band;
        }
        return "flat";
    }

    public static IReadOnlyList<TouPeriod> ParseTou(string json)
    {
        var list = string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<TouPeriod>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        if (list.Count > 48) throw new TagExpressionException("分时时段最多 48 段");
        foreach (var p in list)
        {
            if (!Bands.Contains(p.Band)) throw new TagExpressionException($"分时类型须为 {string.Join("/", Bands)}");
            ParseTime(p.Start); ParseTime(p.End);
            if (p.Start == p.End) throw new TagExpressionException("分时时段起止不能相同");
        }
        return list;
    }

    public static TimeSpan ParseTime(string text) =>
        TimeSpan.TryParseExact(text?.Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var value) && value < TimeSpan.FromDays(1)
            ? value : throw new TagExpressionException($"时间格式须为 HH:mm：{text}");

    public static IReadOnlyList<TimeSpan> ParseShifts(string text)
    {
        var list = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseTime).Distinct().Order().ToList();
        if (list.Count is 0 or > 6) throw new TagExpressionException("班次起点须为 1～6 个 HH:mm");
        return list;
    }

    // 当前所属复位周期：返回周期标识与周期起点（UTC 毫秒）。
    public static (string Key, long StartTs) Period(CalcJob job, long now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(now), zone);
        DateTime start;
        string key;
        switch (job.ResetPeriod)
        {
            case CalcResetPeriod.Day: start = local.Date; key = start.ToString("yyyy-MM-dd"); break;
            case CalcResetPeriod.Month: start = new DateTime(local.Year, local.Month, 1); key = start.ToString("yyyy-MM"); break;
            case CalcResetPeriod.Shift:
                var shifts = ParseShifts(job.ShiftStarts);
                var tod = local.TimeOfDay;
                var current = shifts.LastOrDefault(x => x <= tod, TimeSpan.MinValue);
                start = current == TimeSpan.MinValue ? local.Date.AddDays(-1) + shifts[^1] : local.Date + current;
                key = start.ToString("yyyy-MM-dd HH:mm"); break;
            default: return ("all", 0);
        }
        var offset = zone.GetUtcOffset(start);
        return (key, new DateTimeOffset(start, offset).ToUnixTimeMilliseconds());
    }

    // 周期切换：返回复位前快照并清零周期量。首次运行只记录周期，不复位。
    public static PeriodSnapshot? Roll(CalcJob job, JobRuntimeState state, long now, TimeZoneInfo zone)
    {
        var (key, start) = Period(job, now, zone);
        if (state.PeriodKey == "") { state.PeriodKey = key; state.PeriodStartTs = start; return null; }
        if (state.PeriodKey == key) return null;
        var snapshot = ResetPeriod(state, state.PeriodKey);
        state.PeriodKey = key; state.PeriodStartTs = start;
        return snapshot;
    }

    public static PeriodSnapshot ResetPeriod(JobRuntimeState state, string key)
    {
        var snapshot = new PeriodSnapshot(key, state.PeriodStartTs, new Dictionary<string, double>(state.Values));
        foreach (var name in state.Values.Keys.Where(IsPeriodKey).ToList()) state.Values[name] = 0;
        return snapshot;
    }
}
