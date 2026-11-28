using Scada.Core.Entities;

namespace Scada.Core.Acquisition;

public enum AcquisitionPathKind { Primary = 0, Standby = 1 }

public sealed record DeviceAcquisitionRoute(
    AcquisitionPathKind Path,
    int? GatewayId,
    int? ChannelId,
    string? Host,
    int? Port)
{
    public bool IsLocal => GatewayId is null;
}

public interface IDeviceAcquisitionPaths
{
    DeviceAcquisitionRoute Resolve(Device device);
    Device Apply(Device device);
    bool HasStandby(Device device);
    AcquisitionPathKind Current(int deviceId);
    event Action<int, AcquisitionPathKind, string>? PathChanged;
}

public static class DeviceAcquisitionRules
{
    public const int DefaultFailoverHoldMs = 10_000;
    public const int DefaultSwitchbackHoldMs = 30_000;
    public const int MinHoldMs = 1_000;
    public const int MaxHoldMs = 600_000;

    public static bool HasStandbyConfigured(Device device) =>
        device.StandbyGatewayId is not null || device.StandbyChannelId is not null
            || !string.IsNullOrWhiteSpace(device.StandbyHost) || device.StandbyPort is not null;

    public static bool HasStandby(Device device) =>
        !device.SimulationEnabled && HasStandbyConfigured(device);

    public static string? Validate(Device device)
    {
        if (device.SimulationEnabled && HasStandbyConfigured(device))
            return "仿真设备不能配置采集备路径";
        if (!HasStandby(device)) return null;
        if (SameAsPrimary(device))
            return "备路径不能与主路径相同";
        var standby = StandbyOf(device);
        if (standby.ChannelId is null && (string.IsNullOrWhiteSpace(standby.Host) || standby.Port is not > 0))
            return "备路径需要备用通道，或备用主机和端口";
        if (device.FailoverHoldMs < MinHoldMs || device.FailoverHoldMs > MaxHoldMs)
            return $"切到备路径的保持时间须为 {MinHoldMs / 1000}～{MaxHoldMs / 1000} 秒";
        if (device.SwitchbackHoldMs < device.FailoverHoldMs || device.SwitchbackHoldMs > MaxHoldMs)
            return "回切保持时间须不短于切到备路径的保持时间，且不超过 600 秒";
        return null;
    }

    public static bool SameAsPrimary(Device device)
    {
        if (!HasStandby(device)) return false;
        var primary = PrimaryOf(device);
        var standby = StandbyOf(device);
        return primary.GatewayId == standby.GatewayId
            && primary.ChannelId == standby.ChannelId
            && string.Equals(primary.Host ?? "", standby.Host ?? "", StringComparison.OrdinalIgnoreCase)
            && primary.Port == standby.Port;
    }

    public static DeviceAcquisitionRoute PrimaryOf(Device device) =>
        new(AcquisitionPathKind.Primary, device.GatewayId, device.ChannelId, device.Host, device.Port);

    public static DeviceAcquisitionRoute StandbyOf(Device device)
    {
        var movingGateway = device.StandbyGatewayId is int standbyGateway && standbyGateway != device.GatewayId;
        var gateway = device.StandbyGatewayId ?? device.GatewayId;
        var channel = device.StandbyChannelId ?? (movingGateway ? null : device.ChannelId);
        var host = !string.IsNullOrWhiteSpace(device.StandbyHost) ? device.StandbyHost.Trim() : (movingGateway ? null : device.Host);
        var port = device.StandbyPort ?? (movingGateway ? null : device.Port);
        return new(AcquisitionPathKind.Standby, gateway, channel, host, port);
    }

    public static Device Apply(Device device, DeviceAcquisitionRoute route)
    {
        var clone = device.Clone();
        clone.GatewayId = route.GatewayId;
        clone.ChannelId = route.ChannelId;
        if (route.Host is not null) clone.Host = route.Host;
        if (route.Port is int port) clone.Port = port;
        return clone;
    }
}
