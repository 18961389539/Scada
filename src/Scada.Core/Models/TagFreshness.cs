using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Models;

/// <summary>
/// 与报警引擎相同的陈旧判定：超过 max(15000, 3×有效周期) 视为失效。
/// 对外读取与周期归档共用同一口径，避免冻结的合格值被当成当前值。
/// </summary>
public static class TagFreshness
{
    public const long MinStaleMs = 15_000;

    public static long StaleAfterMs(Tag tag, Device? device) =>
        device is not null
            ? Math.Max(MinStaleMs, ScanCadence.Effective(device, tag) * 3L)
            : Math.Max(MinStaleMs, Math.Max(tag.CalculationIntervalMs, 1) * 3L);

    public static bool IsStale(TagValue value, long nowMs, Tag tag, Device? device) =>
        nowMs - value.Ts > StaleAfterMs(tag, device);

    public static TagValue Apply(TagValue value, long nowMs, Tag tag, Device? device) =>
        IsStale(value, nowMs, tag, device) ? value with { Quality = TagQuality.Bad } : value;

    public static Device? DeviceOf(Tag tag, Func<int, Device?> lookup) =>
        tag.DeviceId is int deviceId ? lookup(deviceId) : null;
}
