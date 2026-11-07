using System.Buffers.Binary;
using Scada.Collector.EtherNetIp;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

public class EtherNetIpTests
{
    private static Device Device(string options = "") => new()
    {
        Id = 1, Name = "eip", Protocol = ProtocolType.EtherNetIp, Host = "127.0.0.1", Port = 44818,
        ProtocolOptions = options
    };

    private static Tag Tag(string address, TagDataType type = TagDataType.Int32) => new()
    {
        DeviceId = 1, Name = "tag", Area = TagArea.Address, NodeId = address, DataType = type
    };

    [Fact]
    public void DeviceValidationRequiresFixedPortSupportedPlcTypeAndPath()
    {
        Assert.Null(ConfigurationValidator.Device(Device()));

        var wrongPort = Device();
        wrongPort.Port = 502;
        Assert.NotNull(ConfigurationValidator.Device(wrongPort));

        Assert.NotNull(ConfigurationValidator.Device(Device("{\"plcType\":\"Unknown\"}")));
        Assert.NotNull(ConfigurationValidator.Device(Device("{\"plcType\":\"Micro800\",\"path\":\"1,0\"}")));
        Assert.NotNull(ConfigurationValidator.Device(Device("{\"plcType\":\"ControlLogix\",\"path\":\"backplane\"}")));
        Assert.Null(ConfigurationValidator.Device(Device("{\"plcType\":\"Micro800\",\"path\":\"\"}")));
        Assert.Null(ConfigurationValidator.Device(Device("{\"plcType\":\"Omron\",\"path\":\"\"}")));
    }

    [Fact]
    public void TagValidationAcceptsCipAndPcccAddresses()
    {
        var device = Device();
        Assert.Null(ConfigurationValidator.Tag(Tag("MyTag"), device));
        Assert.Null(ConfigurationValidator.Tag(Tag("Program:MainProgram.MyTag"), device));
        Assert.Null(ConfigurationValidator.Tag(Tag("MyArray[3]"), device));
        Assert.Null(ConfigurationValidator.Tag(Tag("N7:4"), device));
        Assert.Null(ConfigurationValidator.Tag(Tag("B3:4/2", TagDataType.Bool), device));

        Assert.NotNull(ConfigurationValidator.Tag(Tag("My Tag"), device));
        Assert.NotNull(ConfigurationValidator.Tag(Tag("MyArray[]"), device));
        Assert.NotNull(ConfigurationValidator.Tag(Tag("MyArray[3"), device));
        Assert.NotNull(ConfigurationValidator.Tag(Tag("My..Tag"), device));
    }

    [Fact]
    public void ReadPlanKeepsTextAddressesWithoutRegisterBlocks()
    {
        Assert.True(Scada.Core.TraceabilityAcquisition.SupportsImmediateRead(ProtocolType.EtherNetIp));
        var tags = new[] { Tag("MyTag"), Tag("MyArray[3]", TagDataType.Float) };
        var plan = new ReadPlanBuilder().Build(Device(), tags);
        Assert.Empty(plan.Blocks);
        Assert.Equal(2, plan.Tags.Count);
    }

    [Fact]
    public void ListIdentityParserReadsProductIdentity()
    {
        var item = new byte[41];
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(20), 14);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(22), 100);
        item[24] = 3;
        item[25] = 2;
        item[32] = 7;
        System.Text.Encoding.ASCII.GetBytes("TestPLC").CopyTo(item, 33);

        var payload = new byte[2 + 4 + item.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)item.Length);
        item.CopyTo(payload, 6);

        var identity = EtherNetIpIdentityReader.Parse(payload);
        Assert.NotNull(identity);
        Assert.Equal((ushort)1, identity!.VendorId);
        Assert.Equal((ushort)14, identity.DeviceType);
        Assert.Equal((ushort)100, identity.ProductCode);
        Assert.Equal((byte)3, identity.RevisionMajor);
        Assert.Equal((byte)2, identity.RevisionMinor);
        Assert.Equal("TestPLC", identity.ProductName);
    }

    [Fact]
    public void ListIdentityParserRejectsTruncatedPayload()
    {
        Assert.Null(EtherNetIpIdentityReader.Parse([]));
        Assert.Null(EtherNetIpIdentityReader.Parse(new byte[] { 1, 0 }));
        Assert.Null(EtherNetIpIdentityReader.Parse(new byte[] { 1, 0, 1, 0, 50, 0 }));
    }
}
