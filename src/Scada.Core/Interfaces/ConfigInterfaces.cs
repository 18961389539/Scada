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
    // 只随"建立连接与读数"依赖的配置（设备参数、物理测点定义）变化。
    // 采集引擎据此决定是否重建连接：改报警规则、画面或报表不该让所有设备断线重连。
    // 轻量实现（网关侧、测试替身）可以退化为 Version。
    int CollectionVersion => Version;
    long LoadedAt { get; }
    Task ReloadAsync(CancellationToken cancellationToken = default);
    bool TryGetTag(int tagId, out Tag tag);
    bool TryGetDevice(int deviceId, out Device device);
    bool TryGetArea(int areaId, out Area area) { area = null!; return false; }
}
