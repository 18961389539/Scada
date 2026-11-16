using Scada.Desktop.Runtime;

namespace Scada.Desktop.Tests;

public sealed class HaFailoverCoordinatorTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DebouncesSameTargetForFifteenSeconds()
    {
        var coordinator = new HaFailoverCoordinator();
        Assert.True(coordinator.ShouldAttempt("http://b:5080", Start));
        coordinator.RecordAttempt("http://b:5080", Start);
        Assert.False(coordinator.ShouldAttempt("http://b:5080", Start.AddSeconds(5)));
        Assert.True(coordinator.ShouldAttempt("http://b:5080", Start.AddSeconds(16)));
    }

    [Fact]
    public void BacksOffAndPausesAfterThreeFailures()
    {
        var coordinator = new HaFailoverCoordinator();
        var first = coordinator.RecordFailure("http://b:5080", Start);
        Assert.Equal(30, (int)first!.Value.TotalSeconds);
        Assert.False(coordinator.ShouldAttempt("http://b:5080", Start.AddSeconds(10)));

        var second = coordinator.RecordFailure("http://b:5080", Start.AddSeconds(31));
        Assert.Equal(60, (int)second!.Value.TotalSeconds);

        Assert.Null(coordinator.RecordFailure("http://b:5080", Start.AddSeconds(92)));
        Assert.True(coordinator.Paused);
        Assert.False(coordinator.ShouldAttempt("http://b:5080", Start.AddMinutes(10).AddSeconds(-1)));
    }

    /// <summary>
    /// 无人值守终端不能因为一次瞬时断网就永久停在备机：冷却 10 分钟后重新允许自动切换，
    /// 失败计数从头开始（每个冷却窗口最多 3 次尝试）。
    /// </summary>
    [Fact]
    public void PauseRearmsAfterCooldown()
    {
        var coordinator = new HaFailoverCoordinator();
        coordinator.RecordFailure("http://b:5080", Start);
        coordinator.RecordFailure("http://b:5080", Start.AddSeconds(31));
        coordinator.RecordFailure("http://b:5080", Start.AddSeconds(92));
        Assert.True(coordinator.Paused);

        var due = Start.AddSeconds(92).AddMinutes(10);
        Assert.True(coordinator.ShouldAttempt("http://b:5080", due));
        Assert.False(coordinator.Paused);

        // 重排后从 30 秒重新退避，而不是接着上一轮的计数。
        var retry = coordinator.RecordFailure("http://b:5080", due);
        Assert.Equal(30, (int)retry!.Value.TotalSeconds);
    }

    [Fact]
    public void SuccessAndResetClearPause()
    {
        var coordinator = new HaFailoverCoordinator();
        coordinator.RecordFailure("a", Start);
        coordinator.RecordFailure("a", Start);
        coordinator.RecordFailure("a", Start);
        Assert.True(coordinator.Paused);

        coordinator.RecordSuccess("a");
        Assert.False(coordinator.Paused);

        coordinator.RecordFailure("b", Start);
        coordinator.RecordFailure("b", Start);
        coordinator.RecordFailure("b", Start);
        Assert.True(coordinator.Paused);
        coordinator.Reset();
        Assert.False(coordinator.Paused);
        Assert.True(coordinator.ShouldAttempt("b", Start));
    }

    [Fact]
    public void IgnoresEmptyTarget()
    {
        var coordinator = new HaFailoverCoordinator();
        Assert.False(coordinator.ShouldAttempt(null, Start));
        Assert.False(coordinator.ShouldAttempt(" ", Start));
    }
}
