using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Scripting;

namespace Scada.Core.Protocols;

/// <summary>
/// 串口独占归属：采集通道、未绑定通道的设备、脚本串口配置、追溯串口扫码。
/// 同一通道上的多从站共享串口是合法的，检查时予以保留。
/// </summary>
public sealed record SerialPortOwner(string Kind, string Name, string Port, int? Id = null, int? ChannelId = null);

public static class SerialPortOccupancy
{
    public const string Profile = "profile";
    public const string ConfigFile = "config";
    public const string Channel = "channel";
    public const string Device = "device";
    public const string Scanner = "scanner";

    public static bool SamePort(string? left, string? right) => SerialProfileRules.SamePort(left, right);

    public static string? Conflict(IEnumerable<SerialPortOwner> owners, string port, Func<SerialPortOwner, bool>? ignore = null)
    {
        if (string.IsNullOrWhiteSpace(port)) return null;
        var hit = owners.FirstOrDefault(owner => SamePort(owner.Port, port) && (ignore is null || !ignore(owner)));
        if (hit is null) return null;
        var name = port.Trim();
        return hit.Kind switch
        {
            Profile => $"串口 {name} 已被串口配置「{hit.Name}」占用",
            ConfigFile => $"串口 {name} 已被配置文件中的串口配置占用",
            Channel => $"串口 {name} 已被采集通道「{hit.Name}」占用",
            Device => $"串口 {name} 已被启用设备「{hit.Name}」独占",
            Scanner => $"串口 {name} 已被追溯扫码任务「{hit.Name}」占用",
            _ => $"串口 {name} 已被「{hit.Name}」占用"
        };
    }

    public static IEnumerable<SerialPortOwner> FromProfiles(IEnumerable<SerialProfileConfig> rows, int? exceptId = null) =>
        rows.Where(row => row.Enabled && row.Id != exceptId).Select(row => new SerialPortOwner(Profile, row.Name, row.Port, row.Id));

    public static IEnumerable<SerialPortOwner> FromConfigFile(IReadOnlyDictionary<string, SerialProfile>? configured, string? exceptName = null) =>
        (configured ?? new Dictionary<string, SerialProfile>()).Where(item =>
            !string.Equals(item.Key, exceptName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.Value.Port))
        .Select(item => new SerialPortOwner(ConfigFile, item.Key, item.Value.Port));

    public static IEnumerable<SerialPortOwner> FromChannels(IEnumerable<AcquisitionChannel> channels, int? exceptId = null) =>
        channels.Where(row => row.Enabled && row.Id != exceptId && ChannelProtocols.UsesSerial(row.Protocol))
            .Select(row => new SerialPortOwner(Channel, row.Name, row.SerialPort, row.Id));

    public static IEnumerable<SerialPortOwner> FromExclusiveDevices(IEnumerable<Device> devices, int? exceptId = null) =>
        devices.Where(row => row.Id != exceptId && row.Enabled && row.ChannelId is null && SoftElementByteOrder.OccupiesSerialPort(row))
            .Select(row => new SerialPortOwner(Device, row.Name, row.SerialPort, row.Id));

    public static IEnumerable<SerialPortOwner> FromScanners(IEnumerable<(int Id, string Name, string Port)> jobs, int? exceptId = null) =>
        jobs.Where(job => job.Id != exceptId).Select(job => new SerialPortOwner(Scanner, job.Name, job.Port, job.Id));
}
