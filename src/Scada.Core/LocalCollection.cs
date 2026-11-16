using Scada.Core.Entities;

namespace Scada.Core;

/// <summary>
/// 本机进程内采集的开关语义。默认开启，保持一体化部署。
/// 关闭后 Server 不再连接现场设备；采集、浏览与控制改走已分配网关的 Gateway.Host
/// （可以与中心同机、仅回环通信）。这不是驱动插件进程隔离。
/// </summary>
public static class LocalCollection
{
    public const string DisabledMessage = "本机采集已关闭，请把设备分配到网关后由 Gateway.Host 采集。";
    public const string UnassignedDeviceMessage = "本机采集已关闭时，启用的设备必须分配到网关。";
    public const string DisabledHealthCode = "local_collection_disabled";
    public const string UnassignedHealthCode = "local_collection_unassigned";
    public const string DisabledHealthMessage = "本机采集已关闭；采集、浏览与控制仅走已分配网关的 Gateway.Host。";

    public static string UnassignedHealthMessage(int count) =>
        $"本机采集已关闭，仍有 {count} 台已启用设备未分配网关，这些设备不会被采集。";

    public static bool IsUnassignedEnabled(Device device) => device.Enabled && device.GatewayId is null;
}
