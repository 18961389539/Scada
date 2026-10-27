using Scada.Collector.Modbus;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

public class ProtocolTests
{
    [Fact]
    public void SerialModbusProtocolsRequireSerialParametersAndStationRange()
    {
        var ascii = new Device { Protocol = ProtocolType.ModbusAscii, Name = "ascii", SerialPort = "COM3", BaudRate = 9600, DataBits = 8, StationNumber = 1 };
        Assert.Null(ConfigurationValidator.Device(ascii));
        ascii.StationNumber = 248; Assert.NotNull(ConfigurationValidator.Device(ascii));
        ascii.StationNumber = 1; ascii.SerialPort = ""; Assert.NotNull(ConfigurationValidator.Device(ascii));
        var rtuOverTcp = new Device { Protocol = ProtocolType.ModbusRtuOverTcp, Name = "rtu-tcp", Host = "127.0.0.1", Port = 502, StationNumber = 0 };
        Assert.Null(ConfigurationValidator.Device(rtuOverTcp));
    }

    [Fact]
    public void S7PlanUsesByteOffsetsAndSeparatesAreasAndDataBlocks()
    {
        var device = new Device { Id = 1, Protocol = ProtocolType.SiemensS7, ByteOrder = ByteOrder.DCBA };
        Tag Tag(int id, int address, TagArea area = TagArea.DataBlock, int db = 1, TagDataType type = TagDataType.Float) =>
            new() { Id = id, DeviceId = 1, Name = $"t{id}", Address = address, Area = area, DbNumber = db, DataType = type };
        var tags = new[] { Tag(1, 1), Tag(2, 5, type: TagDataType.Bool), Tag(3, 5, type: TagDataType.Bool), Tag(4, 1, db: 2), Tag(5, 1, TagArea.Memory) };
        tags[2].BitOffset = 7;
        var plan = new ReadPlanBuilder().Build(device, tags);
        var block = plan.Blocks.First(); Assert.Equal(3, plan.Blocks.Count); Assert.Equal(1, block.StartAddress); Assert.Equal(5, block.Length);
        Assert.Equal(new[] { 0, 4, 4 }, block.Tags.Select(x => x.Offset)); Assert.Equal(7, block.Tags[2].BitOffset);
        Assert.All(block.Tags, x => Assert.Equal(ByteOrder.ABCD, x.ByteOrder));
        var many = Enumerable.Range(0, 120).Select(i => Tag(i + 1, i * 4)).ToList();
        Assert.All(new ReadPlanBuilder().Build(device, many).Blocks, x => Assert.InRange(x.Length, 1, 200));
    }

    [Fact]
    public void ProtocolConfigurationRejectsWrongAreasAndAddresses()
    {
        var s7 = new Device { Protocol = ProtocolType.SiemensS7, Name = "S7" };
        var tag = new Tag { DeviceId = 1, Name = "tag", Area = TagArea.DataBlock, DataType = TagDataType.Float, Address = 65533 };
        Assert.NotNull(ConfigurationValidator.Tag(tag, s7)); tag.Address = 100; Assert.Null(ConfigurationValidator.Tag(tag, s7));
        Assert.NotNull(ConfigurationValidator.Tag(tag, new Device()));
        tag.Area = TagArea.Input; tag.Writable = true; Assert.NotNull(ConfigurationValidator.Tag(tag, s7));
        tag.Writable = false; tag.BitOffset = 1; Assert.NotNull(ConfigurationValidator.Tag(tag, s7));
        tag.DataType = TagDataType.Bool; Assert.Null(ConfigurationValidator.Tag(tag, s7));
        Assert.NotNull(ConfigurationValidator.Device(new Device { Protocol = (ProtocolType)99, Name = "invalid" }));
        Assert.NotNull(ConfigurationValidator.Device(new Device { Protocol = ProtocolType.ModbusRtu, Name = "broadcast", StationNumber = 0 }));
    }

}
