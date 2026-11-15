namespace Scada.Desktop.App;

/// <summary>
/// 崩溃重启退避：2s、5s、15s、30s、60s。客户端稳定运行 10 分钟后重新计数；
/// 10 分钟内连续崩溃 5 次后暂停自动重启，避免故障机器无限重启刷日志。
/// </summary>
internal sealed class RestartPolicy
{
    private static readonly int[] DelaysSeconds = [2, 5, 15, 30, 60];
    private readonly List<DateTimeOffset> _crashes = [];

    public TimeSpan? RecordCrash(DateTimeOffset now, TimeSpan runDuration)
    {
        if (runDuration >= TimeSpan.FromMinutes(10)) _crashes.Clear();
        _crashes.RemoveAll(time => now - time > TimeSpan.FromMinutes(10));
        if (_crashes.Count >= 5) return null;
        _crashes.Add(now);
        var seconds = DelaysSeconds[Math.Min(_crashes.Count - 1, DelaysSeconds.Length - 1)];
        return TimeSpan.FromSeconds(seconds);
    }

    public void Reset() => _crashes.Clear();
}
