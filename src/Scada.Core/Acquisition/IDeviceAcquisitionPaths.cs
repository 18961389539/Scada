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
    /// <summary>活动路径集合变化时增加，供历史书写器等在配置版本未变时重载成员。</summary>
    int Version => 0;
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

    public const string SharedBusStandbyMismatch = "共享通道上的设备必须使用相同的备路径，否则切换时会把同一条总线拆到两个采集进程";

    public static bool SharesBusWith(Device left, Device right)
    {
        if (left.Id == right.Id) return false;
        if (left.ChannelId is int channel && channel == right.ChannelId) return true;
        return left.StandbyChannelId is int standby && standby == right.StandbyChannelId;
    }

    public static bool SameStandbyRoute(Device left, Device right)
    {
        if (!HasStandby(left) && !HasStandby(right)) return true;
        if (HasStandby(left) != HasStandby(right)) return false;
        var a = StandbyOf(left); var b = StandbyOf(right);
        if (a.GatewayId != b.GatewayId || a.ChannelId != b.ChannelId || a.Port != b.Port) return false;
        if (!string.Equals(a.Host ?? "", b.Host ?? "", StringComparison.OrdinalIgnoreCase)) return false;
        var aSerial = left.StandbySerialPort ?? "";
        var bSerial = right.StandbySerialPort ?? "";
        if (aSerial.Length == 0 && bSerial.Length == 0) return true;
        return string.Equals(aSerial, bSerial, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static string? ValidateSharedBusStandby(Device device, IEnumerable<Device> peers)
    {
        if (!device.Enabled) return null;
        foreach (var peer in peers)
        {
            if (!peer.Enabled || !SharesBusWith(device, peer)) continue;
            if (!SameStandbyRoute(device, peer)) return SharedBusStandbyMismatch;
        }
        return null;
    }

    public static bool RouteIsLocal(Device device, AcquisitionPathKind path) =>
        path == AcquisitionPathKind.Standby ? StandbyOf(device).IsLocal : PrimaryOf(device).IsLocal;

    public static List<List<Device>> SharedBusGroups(IEnumerable<Device> devices)
    {
        var list = devices.ToList();
        var parent = Enumerable.Range(0, list.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }
        for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
                if (SharesBusWith(list[i], list[j])) Union(i, j);
        return list.Select((device, i) => (device, root: Find(i)))
            .GroupBy(x => x.root)
            .Select(g => g.Select(x => x.device).ToList())
            .ToList();
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
        // 备用网关未填、但填了备用通道或主机时，备路径由中心本机采集（主路径可以是网关）。
        // 若备路径仍走某一网关，应显式填写 StandbyGatewayId（可与主网关相同、但端点不同）。
        var gateway = device.StandbyGatewayId is int standbyGateway
            ? standbyGateway
            : device.GatewayId is not null && HasStandbyEndpoint(device) ? null : device.GatewayId;
        var movingGateway = gateway != device.GatewayId;
        var channel = device.StandbyChannelId ?? (movingGateway ? null : device.ChannelId);
        var host = !string.IsNullOrWhiteSpace(device.StandbyHost) ? device.StandbyHost.Trim() : (movingGateway ? null : device.Host);
        var port = device.StandbyPort ?? (movingGateway ? null : device.Port);
        return new(AcquisitionPathKind.Standby, gateway, channel, host, port);
    }

    private static bool HasStandbyEndpoint(Device device) =>
        device.StandbyChannelId is not null || !string.IsNullOrWhiteSpace(device.StandbyHost) || device.StandbyPort is not null;

    public static Device Apply(Device device, DeviceAcquisitionRoute route)
    {
        var clone = device.Clone();
        clone.GatewayId = route.GatewayId;
        clone.ChannelId = route.ChannelId;
        if (route.Host is not null) clone.Host = route.Host;
        if (route.Port is int port) clone.Port = port;
        if (route.Path == AcquisitionPathKind.Standby)
        {
            if (!string.IsNullOrWhiteSpace(device.StandbySerialPort))
            {
                clone.SerialPort = device.StandbySerialPort;
                if (device.StandbyBaudRate is int baud) clone.BaudRate = baud;
                if (device.StandbyDataBits is int bits) clone.DataBits = bits;
                if (device.StandbyParity is { } parity) clone.Parity = parity;
                if (device.StandbyStopBits is { } stop) clone.StopBits = stop;
            }
            if (device.StandbyTimeoutMs is int timeout) clone.TimeoutMs = timeout;
        }
        return clone;
    }

    public static void ClearStandbyChannelEndpoint(Device device)
    {
        device.StandbySerialPort = null;
        device.StandbyBaudRate = null;
        device.StandbyDataBits = null;
        device.StandbyParity = null;
        device.StandbyStopBits = null;
        device.StandbyTimeoutMs = null;
    }

    public static bool NeedsStandbyChannelStamp(Device device, AcquisitionChannel? channel)
    {
        if (device.StandbyChannelId is null || channel is null || channel.Id != device.StandbyChannelId) return false;
        if (ChannelProtocols.UsesSerial(channel.Protocol))
            return string.IsNullOrWhiteSpace(device.StandbySerialPort);
        return string.IsNullOrWhiteSpace(device.StandbyHost) && device.StandbyPort is null;
    }

    public static bool TryStampUnstampedStandby(Device device, IReadOnlyDictionary<int, AcquisitionChannel> channels)
    {
        if (device.StandbyChannelId is not int channelId || !channels.TryGetValue(channelId, out var channel))
            return false;
        if (!NeedsStandbyChannelStamp(device, channel)) return false;
        StampStandbyFromChannel(device, channel);
        return true;
    }

    public static int StampUnstampedStandby(IEnumerable<Device> devices, IReadOnlyDictionary<int, AcquisitionChannel> channels)
    {
        var count = 0;
        foreach (var device in devices)
            if (TryStampUnstampedStandby(device, channels)) count++;
        return count;
    }

    public static void StampStandbyFromChannel(Device device, AcquisitionChannel channel)
    {
        if (ChannelProtocols.UsesSerial(channel.Protocol))
        {
            device.StandbySerialPort = channel.SerialPort;
            device.StandbyBaudRate = channel.BaudRate;
            device.StandbyDataBits = channel.DataBits;
            device.StandbyParity = channel.Parity;
            device.StandbyStopBits = channel.StopBits;
        }
        else
        {
            device.StandbyHost = channel.Host;
            device.StandbyPort = channel.Port;
            device.StandbySerialPort = null;
            device.StandbyBaudRate = null;
            device.StandbyDataBits = null;
            device.StandbyParity = null;
            device.StandbyStopBits = null;
        }
        device.StandbyTimeoutMs = channel.TimeoutMs;
    }
}
