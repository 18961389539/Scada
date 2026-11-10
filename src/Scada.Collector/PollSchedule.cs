namespace Scada.Collector;

// 采集节拍：把"读完再等一个周期"改成固定栅格，避免长轮询设备的实际周期
// 变成"采集耗时 + PollIntervalMs"而持续漂移。
// 落后超过一个周期（GC 停顿、命令处理、调度延迟）时重新对齐，而不是连续补帧。
internal static class PollSchedule
{
    /// <summary>确定本轮应开始的时间点。0 或落后超过一个周期时对齐到 now。</summary>
    public static long Align(long dueMs, long nowMs, int intervalMs)
        => dueMs == 0 || dueMs < nowMs - intervalMs ? nowMs : dueMs;

    /// <summary>本轮结束后到下一栅格点的等待时长；采集耗时被自动扣减。</summary>
    public static int DelayMs(long dueMs, long nowMs, int intervalMs)
        => (int)Math.Clamp(dueMs - nowMs, 0, intervalMs);

    /// <summary>推进到下一栅格点（固定步长，不吸收采集耗时）。</summary>
    public static long Advance(long dueMs, int intervalMs) => dueMs + intervalMs;
}
