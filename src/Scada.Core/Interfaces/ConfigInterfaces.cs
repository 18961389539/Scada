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
    // 报警规则指纹版本：规则变化时增加，采集版本不必变。
    int AlarmVersion => Version;
    // 对外地址空间指纹版本：测点树、区域或可写标志变化时增加。
    int AddressSpaceVersion => Version;
    long LoadedAt { get; }
    Task ReloadAsync(CancellationToken cancellationToken = default);
    bool TryGetTag(int tagId, out Tag tag);
    bool TryGetDevice(int deviceId, out Device device);
    // 按设备取测点。诊断接口会被 UI 与网关逐设备轮询，默认实现按全表过滤，
    // 正式实现（ConfigSnapshot）应预建分组索引，避免 O(设备×测点) 的重复扫描。
    IReadOnlyList<Tag> TagsOfDevice(int deviceId) => Tags.Where(x => x.DeviceId == deviceId).ToList();
    bool TryGetArea(int areaId, out Area area) { area = null!; return false; }
}
