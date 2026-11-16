using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Tests;

// 串口候选协议表是 EF 查询的粗筛依据，最终"是否真的独占串口"由 SoftElementByteOrder.OccupiesSerialPort 决定。
// 两者口径一旦分叉，漏一个协议就会让串口占用校验静默放过，多一个则会把不该独占的设备算进去。
public sealed class ChannelProtocolsTests
{
    [Fact]
    public void SerialCandidatesMatchUsesSerial()
    {
        foreach (var protocol in Enum.GetValues<ProtocolType>())
            Assert.Equal(ChannelProtocols.UsesSerial(protocol), ChannelProtocols.SerialCandidates.Contains(protocol));
    }

    [Fact]
    public void SerialCandidatesHasNoDuplicates()
    {
        Assert.Equal(ChannelProtocols.SerialCandidates.Length, ChannelProtocols.SerialCandidates.Distinct().Count());
    }
}