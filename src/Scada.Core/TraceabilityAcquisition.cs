using Scada.Core.Enums;

namespace Scada.Core;

/// <summary>哪些协议能在采集周期之外读到当前值。页面、校验和 MCP 使用同一句原因。</summary>
public static class TraceabilityAcquisition
{
    public const string UnsupportedBody =
        "可以采集，但没有即时读取，不能用于追溯。追溯要求 Modbus TCP/RTU/ASCII/RTU-over-TCP、S7、三菱 MC 或欧姆龙 FINS。";

    public const string UnsupportedEnglish =
        "Device #{0} ({1}{2}) can be collected, but it has no immediate read and cannot be used for traceability. Traceability requires Modbus TCP/RTU/ASCII/RTU-over-TCP, S7, Mitsubishi MC or Omron FINS.";

    public static bool SupportsImmediateRead(ProtocolType protocol) =>
        protocol is ProtocolType.ModbusTcp or ProtocolType.ModbusRtu or ProtocolType.ModbusAscii or ProtocolType.ModbusRtuOverTcp
            or ProtocolType.SiemensS7 or ProtocolType.MitsubishiMc or ProtocolType.OmronFins;

    public static string UnsupportedMessage(int deviceId, ProtocolType protocol, int? gatewayId) =>
        $"设备 #{deviceId}（{protocol}{(gatewayId is int id ? $"，经网关 #{id}" : "")}）{UnsupportedBody}";
}
