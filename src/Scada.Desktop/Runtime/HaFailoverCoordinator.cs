namespace Scada.Desktop.Runtime;

/// <summary>
/// HA 切换防抖：同一目标 15 秒内只尝试一次；失败按 30/60 秒退避，第 3 次失败后暂停自动切换
/// 10 分钟——机房断网、主机重启这类瞬时故障过去后，无人值守终端还能自己恢复，不必等到人工重启。
/// 成功一次后清零，不自动切回旧主机。
/// </summary>
internal sealed class HaFailoverCoordinator
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PauseDuration = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, DateTime> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastTarget;
    private DateTime _nextAttemptUtc = DateTime.MinValue;
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private int _failures;
    private bool _paused;

    public bool Paused => _paused;

    public bool ShouldAttempt(string? target, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        if (_paused)
        {
            if (now < _pausedUntilUtc) return false;
            // 冷却结束：重新计数，给一次自动恢复的机会。
            _paused = false;
            _failures = 0;
            _nextAttemptUtc = DateTime.MinValue;
        }

        if (now < _nextAttemptUtc) return false;
        if (string.Equals(target, _lastTarget, StringComparison.OrdinalIgnoreCase)
            && _attempts.TryGetValue(target, out var last)
            && now - last < Debounce)
        {
            return false;
        }
        return true;
    }

    public void RecordAttempt(string target, DateTime now)
    {
        _attempts[target] = now;
        _lastTarget = target;
    }

    public void RecordSuccess(string target)
    {
        _lastTarget = target;
        _failures = 0;
        _nextAttemptUtc = DateTime.MinValue;
        _paused = false;
        _pausedUntilUtc = DateTime.MinValue;
    }

    /// <summary>返回下一次尝试的等待时间；null 表示本轮自动切换已暂停（冷却结束后会重新允许）。</summary>
    public TimeSpan? RecordFailure(string target, DateTime now)
    {
        _lastTarget = target;
        _failures++;
        if (_failures >= 3)
        {
            _failures = 0;
            _paused = true;
            _pausedUntilUtc = now + PauseDuration;
            _nextAttemptUtc = _pausedUntilUtc;
            return null;
        }
        var delay = TimeSpan.FromSeconds(_failures == 1 ? 30 : 60);
        _nextAttemptUtc = now + delay;
        return delay;
    }

    public void Reset()
    {
        _attempts.Clear();
        _lastTarget = null;
        _nextAttemptUtc = DateTime.MinValue;
        _pausedUntilUtc = DateTime.MinValue;
        _failures = 0;
        _paused = false;
    }
}
