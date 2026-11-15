namespace Scada.Core.Scripting;

/// <summary>
/// 串口连续失败后的短暂熔断：设备掉线时每个采集周期都会开关一次 COM 口，
/// 既拖慢脚本，也让端口被驱动反复抢占。逻辑独立成类，便于在无硬件环境下验证。
/// </summary>
public sealed class SerialCircuitBreaker(int failureThreshold = 3, int cooldownMs = 10_000)
{
    private readonly object _sync = new();
    private int _failures;
    private long _cooldownUntil;

    public bool IsCoolingDown(long nowMs)
    {
        lock (_sync) return _cooldownUntil > nowMs;
    }

    public long RemainingMs(long nowMs)
    {
        lock (_sync) return Math.Max(0, _cooldownUntil - nowMs);
    }

    public void Fail(long nowMs)
    {
        lock (_sync)
        {
            if (++_failures < failureThreshold) return;
            _failures = 0;
            _cooldownUntil = nowMs + cooldownMs;
        }
    }

    public void Succeed()
    {
        lock (_sync)
        {
            _failures = 0;
            _cooldownUntil = 0;
        }
    }
}
