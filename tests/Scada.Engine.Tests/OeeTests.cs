using Scada.Engine.Oee;

namespace Scada.Engine.Tests;

// OEE 纯计算：构造区间与产量序列，覆盖跨零点班次、部分窗口、零产量、性能>100%、月末边界。
public class OeeTests
{
    // 2026-02-01 00:00 北京时间（UTC 2026-01-31 16:00）：本地零点且跨月。
    private static readonly long T0 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
    private const long S = 1000, M = 60_000, H = 3_600_000;

    private static OeeInterval Iv(long start, long end, OeeStateKind kind, int? reason = null, bool planned = false) =>
        new(start, end, kind, reason, reason?.ToString(), reason is int r ? $"R{r}" : kind.ToString(), planned);

    private static OeeCountSlice Ct(long start, long end, double good, double reject = 0, double scrap = 0, double ideal = 60, double targetPerHour = 0, int? productId = null) =>
        new(start, end, good, reject, scrap, ideal, targetPerHour, productId, productId is int p ? $"P{p}" : null);

    private static OeeShiftWindow Sh(long start, long end, int id = 1, string name = "A") => new(start, end, id, name);

    private static OeeThresholds Th(Action<OeeThresholds>? edit = null)
    {
        var t = new OeeThresholds(); edit?.Invoke(t); return t;
    }

    [Fact]
    public void AvailabilityPerformanceQualityAndOee()
    {
        // 1 小时班次：运行 45 分钟、故障 10 分钟、停机 5 分钟；理想节拍 60 秒，产量 50 好品 + 5 废品。
        var intervals = new[]
        {
            Iv(T0, T0 + 45 * M, OeeStateKind.Running),
            Iv(T0 + 45 * M, T0 + 55 * M, OeeStateKind.Fault, 1),
            Iv(T0 + 55 * M, T0 + H, OeeStateKind.Stopped, 2)
        };
        var counts = new[] { Ct(T0, T0 + H, good: 50, reject: 5, ideal: 60, targetPerHour: 60) };
        var shifts = new[] { Sh(T0, T0 + H) };
        var r = OeeMath.Analyze(T0, T0 + H, T0 + 2 * H, intervals, counts, shifts, Th());
        Assert.Equal(H, r.PlannedMs);
        Assert.Equal(45 * M, r.RunMs);
        Assert.Equal(15 * M, r.DownMs);
        Assert.Equal(10 * M, r.FaultMs);
        Assert.Equal(0.75, r.Availability);
        // Performance = (60s * 55) / 2700s = 3300/2700 ≈ 1.222 → clamp to 1
        Assert.True(r.PerformanceOver100);
        Assert.True(r.PerformanceClamped);
        Assert.Equal(1.0, r.Performance);
        Assert.Equal(50 / 55.0, r.Quality);
        Assert.Equal(0.75 * 1.0 * (50 / 55.0), r.Oee);
        Assert.Equal(1, r.FaultCount);
        Assert.Equal(45 * M / (double)OeeMath.HourMs, r.MtbfHours);
        Assert.Equal(10 * M / (double)OeeMath.HourMs, r.MttrHours);
    }

    [Fact]
    public void PerformanceOver100WithoutClampKeepsRawValue()
    {
        var intervals = new[] { Iv(T0, T0 + H, OeeStateKind.Running) };
        var counts = new[] { Ct(T0, T0 + H, good: 120, ideal: 60) }; // 理想 60 件/时，实际 120
        var r = OeeMath.Analyze(T0, T0 + H, T0 + H, intervals, counts, [Sh(T0, T0 + H)], Th(t => t.ClampPerformance = false));
        Assert.Equal(2.0, r.Performance);
        Assert.True(r.PerformanceOver100);
        Assert.False(r.PerformanceClamped);
    }

    [Fact]
    public void ZeroProductionGivesZeroPerformance()
    {
        var intervals = new[] { Iv(T0, T0 + H, OeeStateKind.Running) };
        var r = OeeMath.Analyze(T0, T0 + H, T0 + H, intervals, [], [Sh(T0, T0 + H)], Th());
        Assert.Equal(0, r.Performance);
        Assert.Null(r.Quality);
        Assert.Null(r.Oee);
    }

    [Fact]
    public void NoShiftMeansNoPlannedTime()
    {
        var intervals = new[] { Iv(T0, T0 + H, OeeStateKind.Running) };
        var counts = new[] { Ct(T0, T0 + H, good: 10, ideal: 60) };
        var r = OeeMath.Analyze(T0, T0 + H, T0 + H, intervals, counts, [], Th());
        Assert.Equal(0, r.PlannedMs);
        Assert.Equal(0, r.RunMs); // 班次外运行不计入
        Assert.Null(r.Availability);
        Assert.Equal(H, r.OutsideMs);
    }

    [Fact]
    public void OvernightShiftSpansMidnightAndMonthBoundary()
    {
        // 夜班 20:00→08:00，跨越 2 月 1 日零点（也是月末/月初）。
        var shiftStart = T0 - 4 * H; // 前一天 20:00 北京
        var shiftEnd = T0 + 8 * H;   // 当天 08:00
        var intervals = new[]
        {
            Iv(shiftStart, T0 - H, OeeStateKind.Running),
            Iv(T0 - H, T0 + H, OeeStateKind.Fault, 9), // 跨零点故障 2 小时
            Iv(T0 + H, shiftEnd, OeeStateKind.Running)
        };
        var counts = new[] { Ct(shiftStart, shiftEnd, good: 100, reject: 0, ideal: 36) };
        var from = shiftStart; var to = shiftEnd;
        var r = OeeMath.Analyze(from, to, to, intervals, counts, [Sh(shiftStart, shiftEnd, 2, "夜班")], Th());
        Assert.Equal(12 * H, r.PlannedMs);
        Assert.Equal(10 * H, r.RunMs);
        Assert.Equal(2 * H, r.FaultMs);
        Assert.Contains(r.Faults, f => f.StartTs < T0 && f.EndTs > T0); // 故障跨月零点
        Assert.Equal(10 / 12.0, r.Availability!.Value, 6);
        Assert.Single(r.ByShift);
        Assert.Equal("夜班", r.ByShift[0].ShiftName);
    }

    [Fact]
    public void PlannedDowntimeSubtractsFromPlannedTime()
    {
        var intervals = new[]
        {
            Iv(T0, T0 + 30 * M, OeeStateKind.Running),
            Iv(T0 + 30 * M, T0 + 50 * M, OeeStateKind.PlannedDown, 5, planned: true),
            Iv(T0 + 50 * M, T0 + H, OeeStateKind.Running)
        };
        var counts = new[] { Ct(T0, T0 + H, good: 40, ideal: 60) };
        var r = OeeMath.Analyze(T0, T0 + H, T0 + H, intervals, counts, [Sh(T0, T0 + H)], Th());
        Assert.Equal(40 * M, r.PlannedMs); // 60 − 20 计划停机
        Assert.Equal(40 * M, r.RunMs);
        Assert.Equal(20 * M, r.PlannedDownMs);
        Assert.Equal(1.0, r.Availability);
    }

    [Fact]
    public void PartialWindowClipsIntervalsAndCounts()
    {
        var intervals = new[] { Iv(T0 - 30 * M, T0 + 90 * M, OeeStateKind.Running) };
        var counts = new[] { Ct(T0 - 30 * M, T0 + 90 * M, good: 120, ideal: 60) }; // 2 小时共 120 件
        // 只看中间 1 小时
        var r = OeeMath.Analyze(T0, T0 + H, T0 + H, intervals, counts, [Sh(T0 - H, T0 + 2 * H)], Th());
        Assert.Equal(H, r.RunMs);
        Assert.Equal(60, r.Good, 6); // 120 * (60/120)
    }

    [Fact]
    public void DowntimeParetoOrdersByDuration()
    {
        var intervals = new[]
        {
            Iv(T0, T0 + 10 * M, OeeStateKind.Stopped, 1),
            Iv(T0 + 10 * M, T0 + 40 * M, OeeStateKind.Fault, 2),
            Iv(T0 + 40 * M, T0 + 50 * M, OeeStateKind.Stopped, 1),
            Iv(T0 + 50 * M, T0 + H, OeeStateKind.Running)
        };
        var r = OeeMath.Analyze(T0, T0 + H, T0 + H, intervals, [Ct(T0, T0 + H, 10)], [Sh(T0, T0 + H)], Th());
        Assert.Equal(2, r.DowntimePareto.Count);
        Assert.Equal("2", r.DowntimePareto[0].Code); // 30 分钟故障优先
        Assert.Equal(30 * M, r.DowntimePareto[0].Ms);
        Assert.Equal(20 * M, r.DowntimePareto[1].Ms); // 原因 1 共 20 分钟
        Assert.Equal(100, r.DowntimePareto[^1].CumulativePercent);
    }

    [Fact]
    public void ByProductSplitsCounts()
    {
        var intervals = new[] { Iv(T0, T0 + H, OeeStateKind.Running) };
        var counts = new[]
        {
            Ct(T0, T0 + 30 * M, good: 20, ideal: 60, productId: 1),
            Ct(T0 + 30 * M, T0 + H, good: 10, reject: 5, ideal: 90, productId: 2)
        };
        var r = OeeMath.Analyze(T0, T0 + H, T0 + H, intervals, counts, [Sh(T0, T0 + H)], Th());
        Assert.Equal(2, r.ByProduct.Count);
        Assert.Contains(r.ByProduct, p => p.ProductId == 1 && Math.Abs(p.Good - 20) < 0.01);
        Assert.Contains(r.ByProduct, p => p.ProductId == 2 && Math.Abs(p.Reject - 5) < 0.01);
    }

    [Fact]
    public void EffectiveIdealCyclePrefersRatedRate()
    {
        Assert.Equal(36, OeeMath.EffectiveIdealCycleSec(100, 60), 6); // 3600/100
        Assert.Equal(60, OeeMath.EffectiveIdealCycleSec(null, 60));
        Assert.Equal(0, OeeMath.EffectiveIdealCycleSec(0, 0));
    }

    [Fact]
    public void ThresholdValidation()
    {
        Assert.Null(Th().Validate());
        Assert.NotNull(Th(t => t.TargetOee = 1.5).Validate());
        Assert.NotNull(Th(t => t.TopN = 0).Validate());
    }

    [Fact]
    public void HalfOpenWindowExcludesEnd()
    {
        var intervals = new[]
        {
            Iv(T0, T0 + H, OeeStateKind.Running),
            Iv(T0 + H, T0 + 2 * H, OeeStateKind.Fault, 1)
        };
        var r = OeeMath.Analyze(T0, T0 + H, T0 + 2 * H, intervals, [Ct(T0, T0 + H, 10)], [Sh(T0, T0 + 2 * H)], Th());
        Assert.Equal(0, r.FaultMs);
        Assert.Equal(H, r.RunMs);
    }
}
