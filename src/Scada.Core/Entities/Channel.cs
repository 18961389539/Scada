using Scada.Core.Enums;

namespace Scada.Core.Entities;

/// <summary>
/// 物理链路：一条 TCP 连接或一个串口，可被多台从站设备共享。
///
/// 解决的问题：原来 Device 即连接，同一台 Modbus 网关后面的 10 个从站会开 10 条 TCP；
/// 串口更是每台设备各打开一次，第二台必然失败。引入通道后，连接归属通道，站号仍属设备。
///
/// 通道是端点的唯一权威：保存通道时会把端点参数下发到成员设备（Host/Port 或串口参数），
/// 这样写点、诊断、出站白名单、工程导出等既有路径不需要理解通道概念。
/// 只对"一条总线上挂多从站"的协议开放，其余协议（OPC UA、S7、MQTT 等）各自有会话语义，不共享连接。
/// </summary>
public class AcquisitionChannel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ProtocolType Protocol { get; set; } = ProtocolType.ModbusTcp;
    // TCP 端点（ModbusTcp / ModbusRtuOverTcp）
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    // 串口端点（Modbus RTU / ASCII，以及汇川、信捷、台达 RTU）
    public string SerialPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public SerialParity Parity { get; set; }
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;
    /// <summary>链路级超时。通道上的设备共用这条物理链路，因此也共用超时。</summary>
    public int TimeoutMs { get; set; } = 3000;
    public bool Enabled { get; set; } = true;
    public long Revision { get; set; } = 1;

    public AcquisitionChannel Clone() => (AcquisitionChannel)MemberwiseClone();
}

/// <summary>哪些协议允许共享物理链路，以及该协议用 TCP 还是串口端点。</summary>
public static class ChannelProtocols
{
    public static bool Supports(ProtocolType protocol) =>
        protocol is ProtocolType.ModbusTcp or ProtocolType.ModbusRtuOverTcp or ProtocolType.ModbusRtu or ProtocolType.ModbusAscii
            or ProtocolType.Inovance or ProtocolType.Xinje or ProtocolType.Delta;

    /// <summary>
    /// 可能走串口、因而可能独占物理串口的协议。供 EF 查询按协议粗筛候选设备——<c>ProtocolOptions</c> 是 JSON，
    /// SQL 里判不了 RTU/TCP，所以先用这张表缩小范围，最终是否真的独占仍由
    /// <c>SoftElementByteOrder.OccupiesSerialPort</c> 在内存里决定。必须与 <see cref="UsesSerial"/> 保持一致。
    /// </summary>
    public static readonly ProtocolType[] SerialCandidates =
    [
        ProtocolType.ModbusRtu, ProtocolType.ModbusAscii,
        ProtocolType.Inovance, ProtocolType.Xinje, ProtocolType.Delta
    ];

    public static bool UsesSerial(ProtocolType protocol) =>
        protocol is ProtocolType.ModbusRtu or ProtocolType.ModbusAscii
            or ProtocolType.Inovance or ProtocolType.Xinje or ProtocolType.Delta;

    public static bool UsesHostPort(ProtocolType protocol) => Supports(protocol) && !UsesSerial(protocol);

    public static string Describe(ProtocolType protocol) => UsesSerial(protocol) ? "串口" : "TCP";
}
