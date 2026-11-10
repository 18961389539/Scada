using Scada.Core.Enums;

namespace Scada.Core.Entities;

/// <summary>
/// 采集节奏：<see cref="Tag.ScanRateMs"/> 可覆盖设备默认周期，为空时跟随 <see cref="Device.PollIntervalMs"/>。
/// 采集器按有效周期把同一设备的测点分成独立档位，每档各自一份读取计划与节拍。
/// </summary>
public static class ScanCadence
{
    public const int MinMs = 10;
    public const int MaxMs = 3_600_000;

    /// <summary>
    /// 单台设备允许的档位上限。档位越多，同一连接上的读取事务排队越长，
    /// 且慢档会被快档反复打断，收益迅速递减；超过 4 档通常应该拆设备或改用订阅式协议。
    /// </summary>
    public const int MaxLanesPerDevice = 4;

    public static int Effective(Device device, Tag? tag = null)
        => Math.Clamp(tag?.ScanRateMs is int rate && rate > 0 ? rate : device.PollIntervalMs, MinMs, MaxMs);

    /// <summary>设备上所有测点里最慢的有效周期，用于陈旧判定，避免慢档测点被误标超时。</summary>
    public static int SlowestEffective(Device device, IEnumerable<Tag> tags)
    {
        var slowest = Effective(device);
        foreach (var tag in tags) slowest = Math.Max(slowest, Effective(device, tag));
        return slowest;
    }

    /// <summary>校验单个测点的采集周期设置，返回 null 表示通过。</summary>
    public static string? Validate(Tag tag)
    {
        if (tag.ScanRateMs is not int rate) return null;
        if (tag.Kind != TagKind.Physical) return "只有采集测点可以设置采集周期，计算测点请使用计算周期";
        return rate is < MinMs or > MaxMs ? $"采集周期须为 {MinMs}～{MaxMs}ms" : null;
    }

    /// <summary>
    /// 校验档位数量是否超过上限。<paramref name="rates"/> 为同设备（含待保存）测点的采集周期。
    /// </summary>
    public static string? ValidateLaneBudget(IEnumerable<int?> rates)
    {
        var lanes = rates.Where(x => x is > 0).Select(x => x!.Value).Distinct().Count();
        return lanes > MaxLanesPerDevice
            ? $"同一设备最多支持 {MaxLanesPerDevice} 个采集周期档位，当前配置为 {lanes} 个，请合并相近周期"
            : null;
    }

    /// <summary>校验新增一个档位后是否超过上限。</summary>
    public static string? ValidateLaneBudget(IEnumerable<int?> existing, int? candidate)
        => ValidateLaneBudget(candidate is > 0 ? existing.Append(candidate) : existing);
}
