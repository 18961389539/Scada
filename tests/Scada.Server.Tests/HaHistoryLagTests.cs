using Scada.Core.Models;
using Scada.Engine.History;
using Scada.Server.Ha;

namespace Scada.Server.Tests;

public sealed class HaHistoryLagTests
{
    [Fact]
    public void WarnsWhenBehindExceedsRowThresholdOrStreakCannotCatchUp()
    {
        var lag = new HaHistoryLagMonitor();
        lag.Observe(100);
        Assert.Null(lag.Warning());
        lag.Observe(100);
        Assert.Null(lag.Warning());
        lag.Observe(120);
        Assert.Contains("连续 3 轮未能追平", lag.Warning());

        var heavy = new HaHistoryLagMonitor();
        heavy.Observe(HaHistoryLagMonitor.WarnRows);
        Assert.Contains("超过告警阈值", heavy.Warning());

        lag.Observe(0);
        Assert.Null(lag.Warning());
        Assert.Equal(0, lag.Streak);
    }

    [Fact]
    public void CatchingUpDoesNotAccumulateAStreak()
    {
        var lag = new HaHistoryLagMonitor();
        lag.Observe(500);
        lag.Observe(400);
        lag.Observe(300);
        Assert.Equal(1, lag.Streak);
        Assert.Null(lag.Warning());
    }

    [Fact]
    public void PromotionRecordsUnsyncedTailInHistoryLossLog()
    {
        var log = new HistoryLossLog();
        HaHistoryLagMonitor.RecordPromotionGap(log, 0, 1_000, 2_000, "自动接管");
        Assert.Empty(log.Snapshot());

        HaHistoryLagMonitor.RecordPromotionGap(log, 42, 1_000, 2_000, "强制接管");
        var gap = Assert.Single(log.Snapshot());
        Assert.Equal(HistoryGapKind.Rejected, gap.Kind);
        Assert.Equal(1_000, gap.From);
        Assert.Equal(2_000, gap.To);
        Assert.Contains("未同步尾段 42 行", gap.Detail);
        Assert.Contains("强制接管", gap.Detail);
    }
}
