using Scada.Core.Entities;

namespace Scada.Core.Models;

/// <summary>
/// 判断一次组态变更是否会切断或改写已按下的点动目标。调用方应在落盘前先按租约原目标做失电优先释放。
/// </summary>
public static class MomentaryLeaseImpact
{
    public static bool CutsOrRetargets(Tag before, Tag after) =>
        (before.Writable && !after.Writable)
        || before.DeviceId != after.DeviceId
        || before.Address != after.Address
        || before.DbNumber != after.DbNumber
        || before.BitOffset != after.BitOffset
        || !string.Equals(before.NodeId, after.NodeId, StringComparison.Ordinal)
        || before.DataType != after.DataType
        || before.Scale != after.Scale
        || before.Offset != after.Offset
        || before.MinValue != after.MinValue
        || before.MaxValue != after.MaxValue
        || before.TextLength != after.TextLength;

    public static bool CutsDevice(Device before, Device after) =>
        (before.Enabled && !after.Enabled)
        || before.Protocol != after.Protocol
        || before.Host != after.Host
        || before.Port != after.Port
        || before.StationNumber != after.StationNumber
        || before.SerialPort != after.SerialPort
        || before.BaudRate != after.BaudRate
        || before.DataBits != after.DataBits
        || before.Parity != after.Parity
        || before.StopBits != after.StopBits
        || before.ChannelId != after.ChannelId
        || before.GatewayId != after.GatewayId
        || before.Cpu != after.Cpu
        || before.Rack != after.Rack
        || before.Slot != after.Slot
        || before.OpcEndpointUrl != after.OpcEndpointUrl;
}
