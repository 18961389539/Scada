namespace Scada.Collector.Mqtt;

/// <summary>推送型协议使用的“有新值”信号：写入确认时等待对应键的更新。</summary>
internal sealed class UpdateSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Pulse() { TaskCompletionSource old; lock (_gate) { old = _tcs; _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously); } old.TrySetResult(); }
    public Task WaitAsync() { lock (_gate) return _tcs.Task; }

    /// <summary>在截止前反复检查条件，每次有更新时重新判断。</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var wait = WaitAsync();
            if (condition()) return true;
            var left = deadline - Environment.TickCount64;
            if (left <= 0) return false;
            await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMilliseconds(left), ct));
            ct.ThrowIfCancellationRequested();
        }
    }
}
