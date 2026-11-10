namespace Scada.Collector.Tests;

// 采集节拍：固定栅格不应吸收采集耗时，落后一个周期以上要重新对齐而不是补帧。
public sealed class PollScheduleTests
{
    [Fact]
    public void FirstRoundAlignsToNow()
        => Assert.Equal(1_000, PollSchedule.Align(0, 1_000, 500));

    [Fact]
    public void DueWithinOneIntervalIsKept()
        => Assert.Equal(1_000, PollSchedule.Align(1_000, 1_100, 500));

    [Fact]
    public void DueMoreThanOneIntervalBehindRealignsToNow()
        => Assert.Equal(2_001, PollSchedule.Align(1_000, 2_001, 500));

    [Fact]
    public void DelaySubtractsWorkDuration()
    {
        // 本轮 1000ms 开始、采集耗时 120ms、周期 500ms → 还应等 380ms
        Assert.Equal(380, PollSchedule.DelayMs(1_500, 1_120, 500));
    }

    [Fact]
    public void DelayIsNeverNegative()
        => Assert.Equal(0, PollSchedule.DelayMs(1_000, 1_600, 500));

    [Fact]
    public void DelayIsCappedAtOneInterval()
        => Assert.Equal(500, PollSchedule.DelayMs(9_999, 1_000, 500));

    // 复刻 RunCoreAsync 的真实顺序：对齐本轮起点 → 采集 → 推进到下一栅格 → 计算等待。
    // 若直接拿本轮起点算等待会得到 0，采集器启动后会立刻多读一轮（进度指标与"最近采样"都会跳变）。
    [Fact]
    public void FirstRoundWaitsUntilNextGrid()
    {
        var due = PollSchedule.Align(0, 1_000, 60_000);
        var nextDue = PollSchedule.Advance(due, 60_000);
        Assert.Equal(59_995, PollSchedule.DelayMs(nextDue, 1_005, 60_000));
    }

    [Fact]
    public void LongStallRealignsToNow()
        => Assert.Equal(200_000, PollSchedule.Align(1_000, 200_000, 60_000));

    [Fact]
    public void CadenceDoesNotDriftWithWorkDuration()
    {
        // 每轮耗时不同（全部小于周期），下一时点必须始终落在 500ms 的整数倍栅格上，
        // 等待时长则是"周期减去本轮耗时"。
        long due = 1_000;
        long[] workDurations = [37, 120, 8, 260, 95, 400, 12, 180];
        foreach (var work in workDurations)
        {
            var finish = due + work;
            due = PollSchedule.Advance(due, 500);
            Assert.Equal(500 - work, PollSchedule.DelayMs(due, finish, 500));
        }
        Assert.Equal(1_000 + workDurations.Length * 500, due);
        Assert.Equal(0, due % 500);
    }
}
