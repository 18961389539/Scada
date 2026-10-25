using System.Text.Json;
using Scada.Collector.Omron;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Collector.Tests;

public class OmronFinsTests
{
    private static Tag T(int id, string address, TagDataType type, bool writable = false) =>
        new() { Id = id, DeviceId = 1, Name = $"t{id}", Area = TagArea.Address, NodeId = address, DataType = type, Writable = writable };

    [Theory]
    [InlineData("DM100", "DM", 100, null)]
    [InlineData("d32767", "DM", 32767, null)]
    [InlineData("CIO10.05", "CIO", 10, 5)]
    [InlineData("W3.15", "WR", 3, 15)]
    [InlineData("H10", "HR", 10, null)]
    [InlineData("A100", "AR", 100, null)]
    [InlineData("T5", "TIM", 5, null)]
    [InlineData("CNT5", "CNT", 0x8005, null)]
    public void ParsesAddresses(string text, string area, int word, int? bit)
    {
        Assert.True(FinsAddress.TryParse(text, out var a, out var e), e);
        Assert.Equal(area, a.Area.Name); Assert.Equal(word, a.Word); Assert.Equal(bit, a.Bit);
    }

    [Theory]
    [InlineData("DM40000")]
    [InlineData("CIO1.16")]
    [InlineData("T1.0")]
    [InlineData("X10")]
    public void RejectsAddresses(string text) => Assert.False(FinsAddress.TryParse(text, out _, out _));

    [Fact]
    public void DeviceValidationRequiresUdpNodes()
    {
        var device = new Device { Name = "f", Protocol = ProtocolType.OmronFins, Host = "plc", Port = 9600 };
        Assert.Contains("节点号", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"destNode\":10,\"sourceNode\":10}"; Assert.Contains("不能与 PLC 相同", ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"destNode\":10,\"sourceNode\":20}"; Assert.Null(ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"transport\":\"Tcp\"}"; Assert.Null(ConfigurationValidator.Device(device));
        Assert.Contains("位号", ConfigurationValidator.Tag(T(1, "DM1", TagDataType.Bool), device));
        Assert.Contains("Bool", ConfigurationValidator.Tag(T(1, "DM1.1", TagDataType.Int16), device));
        Assert.Contains("越界", ConfigurationValidator.Tag(T(1, "DM32767", TagDataType.Float), device));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsAndWritesAgainstSimulator(bool tcp)
    {
        await using var plc = new FinsSimulator(tcp);
        plc.Memory[0x82][100] = 0xFFFF; plc.Memory[0x82][101] = 1234;
        var f = BitConverter.SingleToUInt32Bits(-3.5f); plc.Memory[0x82][102] = (ushort)f; plc.Memory[0x82][103] = (ushort)(f >> 16);
        plc.Memory[0xB0][10] = 1 << 5; plc.Memory[0x89][5] = 77; plc.Memory[0x89][0x8005] = 88;
        var options = tcp ? (object)new { transport = "Tcp" } : new { transport = "Udp", destNode = 10, sourceNode = 20 };
        var device = new Device { Id = 1, Name = "f", Protocol = ProtocolType.OmronFins, Host = "127.0.0.1", Port = plc.Port, TimeoutMs = 1000, ProtocolOptions = JsonSerializer.Serialize(options) };
        Assert.Null(ConfigurationValidator.Device(device));
        var tags = new[] { T(1, "D100", TagDataType.Int16), T(2, "D101", TagDataType.UInt16, true), T(3, "D102", TagDataType.Float, true),
            T(4, "CIO10.05", TagDataType.Bool, true), T(5, "CIO10.06", TagDataType.Bool, true), T(6, "T5", TagDataType.UInt16), T(7, "CNT5", TagDataType.UInt16), T(8, "H0", TagDataType.Int32, true) };
        await using var driver = new OmronFinsDriver();
        await driver.ConnectAsync(device);
        if (tcp) { Assert.Equal(33, driver.SourceNode); Assert.Equal(10, driver.DestNode); }
        var plan = new ReadPlan { Tags = tags };
        var v = (await driver.ReadAsync(device, plan)).ToDictionary(x => x.TagId);
        Assert.Equal(-1, v[1].Value); Assert.Equal(1234, v[2].Value); Assert.Equal(-3.5, v[3].Value);
        Assert.Equal(1, v[4].Value); Assert.Equal(0, v[5].Value); Assert.Equal(77, v[6].Value); Assert.Equal(88, v[7].Value);
        Assert.Equal(5, driver.LastRequestCount); // DM、CIO、TIM、CNT（同区域码 0x89 但相距过远）、HR
        await driver.WriteAsync(device, tags[4], 1);
        Assert.Equal((1 << 5) | (1 << 6), plc.Memory[0xB0][10]); // 位写不影响相邻位
        await driver.WriteAsync(device, tags[3], 0);
        Assert.Equal(1 << 6, plc.Memory[0xB0][10]);
        await driver.WriteAsync(device, tags[2], 99.5);
        await driver.WriteAsync(device, tags[7], -70000);
        v = (await driver.ReadAsync(device, plan)).ToDictionary(x => x.TagId);
        Assert.Equal(99.5, v[3].Value); Assert.Equal(-70000, v[8].Value);
        Assert.Equal(unchecked((ushort)-70000), plc.Memory[0xB2][0]);
    }

    [Fact]
    public async Task EndCodesAndSidMismatchAreReported()
    {
        await using var plc = new FinsSimulator(false);
        plc.Memory.Remove(0xB3);
        var device = new Device { Id = 1, Name = "f", Protocol = ProtocolType.OmronFins, Host = "127.0.0.1", Port = plc.Port, TimeoutMs = 500, ProtocolOptions = "{\"destNode\":10,\"sourceNode\":20}" };
        await using var driver = new OmronFinsDriver();
        await driver.ConnectAsync(device);
        var v = (await driver.ReadAsync(device, new ReadPlan { Tags = [T(1, "A0", TagDataType.Int16), T(2, "D0", TagDataType.Int16)] })).ToDictionary(x => x.TagId);
        Assert.Equal(TagQuality.Bad, v[1].Quality); Assert.Contains("1101", v[1].Error); Assert.Equal(TagQuality.Good, v[2].Quality);
        plc.CorruptSid = true;
        var ex = await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(device, new ReadPlan { Tags = [T(2, "D0", TagDataType.Int16)] }));
        Assert.Contains("SID", ex.Message);
    }
}
