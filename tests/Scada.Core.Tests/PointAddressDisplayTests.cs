using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Protocols;

namespace Scada.Core.Tests;

// 诊断面板的「连接」「地址」两列是现场排障的第一手信息，协议分派怎么重构都不能改变输出文本。
// 这里先把现有行为逐条写死，再作为收敛 DeviceLink.Describe 的护栏（不改实现也能先跑绿）。
public sealed class PointAddressDisplayTests
{
    private static DeviceLink Link(ProtocolType protocol, Action<Device>? edit = null)
    {
        var device = new Device
        {
            Protocol = protocol,
            Host = "10.0.0.1",
            Port = 502,
            StationNumber = 1,
            SerialPort = "COM3",
            BaudRate = 9600,
            DataBits = 8,
            Parity = SerialParity.None,
            StopBits = SerialStopBits.One,
            ByteOrder = ByteOrder.ABCD
        };
        edit?.Invoke(device);
        return DeviceLink.From(device);
    }

    private static Tag Tag(int address = 0, TagArea area = TagArea.HoldingRegister, TagDataType type = TagDataType.Int16) =>
        new() { Address = address, Area = area, DataType = type };

    [Fact]
    public void DescribeOpcUaShowsEndpointAndSecurity()
    {
        var link = Link(ProtocolType.OpcUa, d =>
        {
            d.OpcEndpointUrl = "opc.tcp://plc:4840";
            d.OpcSecurityMode = OpcSecurityMode.SignAndEncrypt;
            d.OpcSecurityPolicy = OpcSecurityPolicy.Basic256Sha256;
        });
        Assert.Equal("opc.tcp://plc:4840 · SignAndEncrypt · Basic256Sha256", link.Describe());
    }

    [Fact]
    public void DescribeSiemensShowsCpuAndRackSlot()
    {
        var link = Link(ProtocolType.SiemensS7, d => { d.Port = 102; d.Cpu = S7Cpu.S71500; d.Rack = 0; d.Slot = 1; });
        Assert.Equal("10.0.0.1:102 · S71500 · Rack 0 / Slot 1", link.Describe());
    }

    [Theory]
    [InlineData(ProtocolType.ModbusRtu)]
    [InlineData(ProtocolType.ModbusAscii)]
    public void DescribeModbusSerialShowsSerialSettings(ProtocolType protocol)
    {
        Assert.Equal("COM3 · 9600/8/None/One · 站号 1", Link(protocol).Describe());
    }

    [Theory]
    [InlineData(ProtocolType.Inovance, "{\"series\":\"H3U\",\"transport\":\"Rtu\"}")]
    [InlineData(ProtocolType.Xinje, "{\"series\":\"XC\",\"transport\":\"Rtu\"}")]
    [InlineData(ProtocolType.Delta, "{\"series\":\"DVP\",\"transport\":\"Rtu\"}")]
    public void DescribeVendorRtuShowsSerialSettings(ProtocolType protocol, string options)
    {
        Assert.Equal("COM3 · 9600/8/None/One · 站号 1", Link(protocol, d => d.ProtocolOptions = options).Describe());
    }

    // 汇川/信捷/台达同样在 ProtocolCatalog 里（IsTextAddress 为 true），TCP 变体必须继续输出
    // 「站号 + 字节序」，不能被文本协议分支截走成「协议 · host:port」。
    [Theory]
    [InlineData(ProtocolType.Inovance, "{\"series\":\"H3U\",\"transport\":\"Tcp\"}")]
    [InlineData(ProtocolType.Xinje, "{\"series\":\"XC\",\"transport\":\"Tcp\"}")]
    [InlineData(ProtocolType.Delta, "{\"series\":\"DVP\",\"transport\":\"Tcp\"}")]
    public void DescribeVendorTcpIsNotTreatedAsTextAddress(ProtocolType protocol, string options)
    {
        Assert.Equal("10.0.0.1:502 · 站号 1 · ABCD", Link(protocol, d => d.ProtocolOptions = options).Describe());
    }

    [Theory]
    [InlineData(ProtocolType.ModbusTcp)]
    [InlineData(ProtocolType.ModbusRtuOverTcp)]
    public void DescribePlainTcpShowsStationAndByteOrder(ProtocolType protocol)
    {
        Assert.Equal("10.0.0.1:502 · 站号 1 · ABCD", Link(protocol).Describe());
    }

    [Theory]
    [InlineData(ProtocolType.Mqtt, 1883)]
    [InlineData(ProtocolType.SparkplugB, 1883)]
    [InlineData(ProtocolType.Iec104, 2404)]
    [InlineData(ProtocolType.MitsubishiMc, 5000)]
    [InlineData(ProtocolType.OpcDa, 135)]
    public void DescribeTextProtocolsShowProtocolAndEndpoint(ProtocolType protocol, int port)
    {
        Assert.Equal($"{protocol} · 10.0.0.1:{port}", Link(protocol, d => d.Port = port).Describe());
    }

    [Fact]
    public void DisplayOpcUaReturnsNodeId()
    {
        var tag = Tag(area: TagArea.OpcNode);
        tag.NodeId = "ns=2;s=Line1.Speed";
        Assert.Equal("ns=2;s=Line1.Speed", PointAddress.Display(ProtocolType.OpcUa, tag));
    }

    [Fact]
    public void DisplayTextAreaReturnsNodeIdForAnyProtocol()
    {
        var tag = Tag(area: TagArea.Address);
        tag.NodeId = "plant/a";
        Assert.Equal("plant/a", PointAddress.Display(ProtocolType.Mqtt, tag));
    }

    [Fact]
    public void DisplayS7IncludesDbAndBit()
    {
        var tag = Tag(100, TagArea.DataBlock, TagDataType.Bool);
        tag.DbNumber = 1;
        tag.BitOffset = 3;
        Assert.Equal("DB1:100.3", PointAddress.Display(ProtocolType.SiemensS7, tag));
    }

    [Fact]
    public void DisplayS7WithoutBitOmitsSuffix()
    {
        var tag = Tag(100, TagArea.DataBlock, TagDataType.Int16);
        tag.DbNumber = 1;
        Assert.Equal("DB1:100", PointAddress.Display(ProtocolType.SiemensS7, tag));
    }

    [Fact]
    public void DisplayS7NonDataBlockUsesAreaName()
    {
        Assert.Equal("Input:50", PointAddress.Display(ProtocolType.SiemensS7, Tag(50, TagArea.Input)));
    }

    [Theory]
    [InlineData(TagArea.HoldingRegister, 100, "HoldingRegister:100")]
    [InlineData(TagArea.Coil, 5, "Coil:5")]
    [InlineData(TagArea.DiscreteInput, 7, "DiscreteInput:7")]
    public void DisplayRegisterAreaUsesEnumName(TagArea area, int address, string expected)
    {
        Assert.Equal(expected, PointAddress.Display(ProtocolType.ModbusTcp, Tag(address, area)));
    }
}