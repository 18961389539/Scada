using Scada.Core.Entities;

namespace Scada.Core.Interfaces;

public interface IConfigSnapshot
{
    IReadOnlyList<Device> Devices { get; }
    IReadOnlyList<Tag> Tags { get; }
    // 区域名用于报警文案；轻量实现（网关侧、测试替身）可以不提供。
    IReadOnlyList<Area> Areas => [];
    IReadOnlyList<AlarmRule> AlarmRules { get; }
    int Version { get; }
    long LoadedAt { get; }
    Task ReloadAsync(CancellationToken cancellationToken = default);
    bool TryGetTag(int tagId, out Tag tag);
    bool TryGetDevice(int deviceId, out Device device);
    bool TryGetArea(int areaId, out Area area) { area = null!; return false; }
}
