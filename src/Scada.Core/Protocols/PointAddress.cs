using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

/// <summary>
/// 测点地址只有一种解释。文本协议和 OPC UA 用 <see cref="Tag.NodeId"/>；
/// Modbus 用区域加寄存器号；S7 用区域、DB、字节和位。同一测点不能两套同时填写。
/// </summary>
public static class PointAddress
{
    public static int RegisterCount(TagDataType dataType) => dataType switch
    {
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Double => 4,
        _ => 1
    };

    // 字符串按字节长度占寄存器；字符按高字节在前排列，长度由 Tag.TextLength 决定。
    public static int RegisterCount(Tag tag) => tag.DataType == TagDataType.String ? (tag.TextLength + 1) / 2 : RegisterCount(tag.DataType);

    // S7 STRING 有 2 字节头（最大长度、实际长度）。
    public static int ByteCount(Tag tag) => tag.DataType switch
    {
        TagDataType.Bool => 1,
        TagDataType.String => tag.TextLength + 2,
        _ => RegisterCount(tag.DataType) * 2
    };

    public static bool IsText(Tag tag) => tag.Area is TagArea.Address or TagArea.OpcNode;

    /// <summary>寄存器地址与文本地址不能同时有值。计算测点和派生测点不使用现场地址。</summary>
    public static string? ValidateShape(Tag tag)
    {
        if (tag.Kind != TagKind.Physical) return null;
        if (IsText(tag))
        {
            if (tag.Address != 0 || tag.BitOffset != 0) return "文本地址不能同时填写寄存器地址或位偏移";
            return null;
        }
        if (!string.IsNullOrEmpty(tag.NodeId)) return "寄存器地址不能同时填写文本地址";
        return null;
    }

    public static string Display(ProtocolType protocol, Tag tag) => protocol switch
    {
        ProtocolType.OpcUa => tag.NodeId,
        _ when tag.Area == TagArea.Address => tag.NodeId,
        ProtocolType.SiemensS7 => $"{(tag.Area == TagArea.DataBlock ? $"DB{tag.DbNumber}" : tag.Area.ToString())}:{tag.Address}{(tag.DataType == TagDataType.Bool ? $".{tag.BitOffset}" : "")}",
        _ => $"{tag.Area}:{tag.Address}"
    };
}

/// <summary>
/// 设备连接参数的唯一读模型。协议专用列仍存在库表里，驱动和诊断都从这里取，不再各自挑列。
/// </summary>
public readonly record struct DeviceLink(
    ProtocolType Protocol,
    string Host,
    int Port,
    byte Station,
    ByteOrder ByteOrder,
    int TimeoutMs,
    string SerialPort,
    int BaudRate,
    int DataBits,
    SerialParity Parity,
    SerialStopBits StopBits,
    S7Cpu Cpu,
    short Rack,
    short Slot,
    string OpcEndpointUrl,
    OpcSecurityMode OpcSecurityMode,
    OpcSecurityPolicy OpcSecurityPolicy,
    string OpcUserName,
    string? OpcPasswordProtected,
    string ProtocolOptions)
{
    public static DeviceLink From(Device device) => new(
        device.Protocol, device.Host, device.Port, device.StationNumber, device.ByteOrder, device.TimeoutMs,
        device.SerialPort, device.BaudRate, device.DataBits, device.Parity, device.StopBits,
        device.Cpu, device.Rack, device.Slot, device.OpcEndpointUrl, device.OpcSecurityMode, device.OpcSecurityPolicy,
        device.OpcUserName, device.OpcPasswordProtected, device.ProtocolOptions);

    public string Describe() => Protocol switch
    {
        ProtocolType.OpcUa => $"{OpcEndpointUrl} · {OpcSecurityMode} · {OpcSecurityPolicy}",
        ProtocolType.ModbusRtu or ProtocolType.ModbusAscii => $"{SerialPort} · {BaudRate}/{DataBits}/{Parity}/{StopBits} · 站号 {Station}",
        ProtocolType.SiemensS7 => $"{Host}:{Port} · {Cpu} · Rack {Rack} / Slot {Slot}",
        _ when ProtocolCatalog.IsTextAddress(Protocol) => $"{Protocol} · {Host}:{Port}",
        _ => $"{Host}:{Port} · 站号 {Station} · {ByteOrder}"
    };
}
