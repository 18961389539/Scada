using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

/// <summary>
/// 汇川、信捷、台达的 32 位默认低字在前。平台设备的默认字节序是 ABCD，
/// 创建和导入时若没另选，按 CDAB 保存和读写。显式选择的 BADC、CDAB、DCBA 保持不变。
/// </summary>
public static class SoftElementByteOrder
{
    public static bool Applies(ProtocolType protocol) =>
        protocol is ProtocolType.Inovance or ProtocolType.Xinje or ProtocolType.Delta;

    public static void Normalize(Device device)
    {
        if (Applies(device.Protocol) && device.ByteOrder == ByteOrder.ABCD)
            device.ByteOrder = ByteOrder.CDAB;
    }

    public static ByteOrder Effective(Device device) =>
        Applies(device.Protocol) && device.ByteOrder == ByteOrder.ABCD ? ByteOrder.CDAB : device.ByteOrder;

    public static bool IsRtu(Device device) => device.Protocol switch
    {
        ProtocolType.Inovance => InovanceAddress.UsesSerial(device),
        ProtocolType.Xinje => XinjeAddress.UsesSerial(device),
        ProtocolType.Delta => DeltaAddress.UsesSerial(device),
        _ => false
    };

    /// <summary>这台设备会独占打开串口：Modbus RTU/ASCII，或这三家的 RTU。</summary>
    public static bool OccupiesSerialPort(Device device) =>
        device.Protocol is ProtocolType.ModbusRtu or ProtocolType.ModbusAscii || IsRtu(device);
}
