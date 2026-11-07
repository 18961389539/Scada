using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public sealed class EtherNetIpDeviceOptions
{
    /// <summary>PLC 型号：ControlLogix/CompactLogix/Micro800/Slc500/Plc5/LogixPccc/MicroLogix/Omron。</summary>
    public string PlcType { get; set; } = "ControlLogix";
    /// <summary>背板路由，例如 1,0；Micro800 不使用路由，必须留空。</summary>
    public string Path { get; set; } = "1,0";
    /// <summary>允许 CIP 多请求打包；个别老设备不兼容时可关闭。</summary>
    public bool AllowPacking { get; set; } = true;
}

/// <summary>
/// EtherNet/IP（CIP 显式报文）设备与测点校验。测点地址为 PLC 标签名，
/// 由 libplctag 解析；Bool 测点可使用 BOOL 标签或 PCCC 位地址（如 B3:4/2）。
/// </summary>
public sealed partial class EtherNetIpSpec : IProtocolSpec
{
    private static readonly HashSet<string> PlcTypes =
    [
        "ControlLogix", "CompactLogix", "Micro800", "Slc500", "Plc5", "LogixPccc", "MicroLogix", "Omron"
    ];

    public ProtocolType Protocol => ProtocolType.EtherNetIp;
    public bool UsesHostPort => true;
    public bool UsesCredentials => false;

    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<EtherNetIpDeviceOptions>(device, out var o) is string error) return error;
        // libplctag 的 ab_eip 固定使用 44818，不能像 Modbus 一样自定义端口。
        if (device.Port != 44818) return "EtherNet/IP 使用固定端口 44818";
        var plcType = o.PlcType ?? "";
        if (!PlcTypes.Contains(plcType)) return "PLC 类型须为 ControlLogix/CompactLogix/Micro800/Slc500/Plc5/LogixPccc/MicroLogix/Omron";
        var path = (o.Path ?? "").Trim();
        if (path.Length > 64) return "背板路由过长（最多 64 个字符）";
        if (plcType == "Micro800" && path.Length > 0) return "Micro800 不使用背板路由，请将路由留空";
        if (path.Length > 0 && !PathPattern().IsMatch(path)) return "背板路由须为数字逗号分隔，例如 1,0";
        return null;
    }

    public string? ValidateTag(Tag tag, Device? device)
    {
        var address = (tag.NodeId ?? "").Trim();
        if (address.Length is < 1 or > 1024) return "标签名须为 1～1024 个字符";
        if (!AddressPattern().IsMatch(address)) return "标签名只能包含字母、数字、下划线、点、冒号、方括号、逗号和斜杠";
        if (address.Contains("[]")) return "数组下标不能为空";
        if (address.Count(ch => ch == '[') != address.Count(ch => ch == ']')) return "数组下标不完整";
        if (address.Contains("..")) return "标签名不能包含连续的点";
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.:\[\],/]*$")]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"^\d+(,\d+)*$")]
    private static partial Regex PathPattern();
}
