using System.Text.Json;
using Scada.Collector.Mitsubishi;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Collector.Tests;

public class MitsubishiMcTests
{
    private static Tag T(int id, string address, TagDataType type, bool writable = false) =>
        new() { Id = id, DeviceId = 1, Name = $"t{id}", Area = TagArea.Address, NodeId = address, DataType = type, Writable = writable };

    [Theory]
    [InlineData("D100", "D", 100, null)]
    [InlineData("d100.f", "D", 100, 15)]
    [InlineData("X1F", "X", 0x1F, null)]
    [InlineData("XA0", "X", 0xA0, null)]
    [InlineData("DX10", "DX", 0x10, null)]
    [InlineData("ZR1000", "ZR", 1000, null)]
    [InlineData("SM400", "SM", 400, null)]
    [InlineData("W1A0", "W", 0x1A0, null)]
    [InlineData("BA", "B", 0xA, null)]
    public void ParsesAddresses(string text, string device, int number, int? bit)
    {
        Assert.True(McAddress.TryParse(text, out var a, out var error), error);
        Assert.Equal(device, a.Device.Name); Assert.Equal(number, a.Number); Assert.Equal(bit, a.Bit);
    }

    [Theory]
    [InlineData("Q10")]
    [InlineData("DA")]
    [InlineData("M1.2")]
    [InlineData("D")]
    public void RejectsAddresses(string text) => Assert.False(McAddress.TryParse(text, out _, out _));

    [Fact]
    public void TagValidation()
    {
        var device = new Device { Name = "mc", Protocol = ProtocolType.MitsubishiMc, Host = "plc", Port = 5000 };
        Assert.Null(ConfigurationValidator.Device(device));
        Assert.Contains("Bool", ConfigurationValidator.Tag(T(1, "M10", TagDataType.Int16), device));
        Assert.Contains("位号", ConfigurationValidator.Tag(T(1, "D10", TagDataType.Bool), device));
        Assert.Contains("只读", ConfigurationValidator.Tag(T(1, "D10.1", TagDataType.Bool, true), device));
        Assert.Null(ConfigurationValidator.Tag(T(1, "D10", TagDataType.Float, true), device));
        device.ProtocolOptions = "{\"networkNo\":300}"; Assert.NotNull(ConfigurationValidator.Device(device));
        device.ProtocolOptions = "{\"opcUserName\":1}"; Assert.NotNull(ConfigurationValidator.Device(device));
        device.ProtocolOptions = ""; device.OpcUserName = "x"; Assert.Contains("用户名", ConfigurationValidator.Device(device));
    }

    [Fact]
    public void BinaryReadFrameMatchesManualExample()
    {
        // MC 手册示例：D100 起读 3 点（字单位），网络 0、PC FF、IO 03FF、站 0、监视定时器 0x0010
        var o = new McDeviceOptions { MonitoringTimer = 0x10 };
        var frame = McFrame.Request(o, McFrame.CmdBatchRead, McFrame.SubWord, McAddress.Devices["D"], 100, 3);
        Assert.Equal(Convert.FromHexString("500000FFFF03000C001000010400006400 00A80300".Replace(" ", "")), frame);
        o.Format = McFormat.Ascii;
        Assert.Equal("500000FF03FF000018001004010000D*0001000003", System.Text.Encoding.ASCII.GetString(McFrame.Request(o, McFrame.CmdBatchRead, McFrame.SubWord, McAddress.Devices["D"], 100, 3)));
    }

    [Theory]
    [InlineData(McFormat.Binary, IpTransport.Tcp)]
    [InlineData(McFormat.Ascii, IpTransport.Tcp)]
    [InlineData(McFormat.Binary, IpTransport.Udp)]
    [InlineData(McFormat.Ascii, IpTransport.Udp)]
    public async Task ReadsAndWritesAgainstSimulator(McFormat format, IpTransport transport)
    {
        await using var plc = new McSimulator(format, transport == IpTransport.Udp);
        plc.Words["D"][100] = 0xFFFE; plc.Words["D"][101] = 0x8001;
        var f = BitConverter.SingleToUInt32Bits(12.5f); plc.Words["D"][102] = (ushort)f; plc.Words["D"][103] = (ushort)(f >> 16);
        plc.Bits["M"][10] = true; plc.Bits["X"][0x1F] = true; plc.Words["W"][0x10] = 0b1000;
        var device = new Device { Id = 1, Name = "mc", Protocol = ProtocolType.MitsubishiMc, Host = "127.0.0.1", Port = plc.Port, TimeoutMs = 1000,
            ProtocolOptions = JsonSerializer.Serialize(new { format = format.ToString(), transport = transport.ToString() }) };
        Assert.Null(ConfigurationValidator.Device(device));
        var tags = new[] { T(1, "D100", TagDataType.Int16), T(2, "D101", TagDataType.UInt16, true), T(3, "D102", TagDataType.Float, true),
            T(4, "M10", TagDataType.Bool, true), T(5, "M11", TagDataType.Bool), T(6, "X1F", TagDataType.Bool), T(7, "W10.3", TagDataType.Bool), T(8, "D200", TagDataType.Int32, true) };
        await using var driver = new MitsubishiMcDriver();
        await driver.ConnectAsync(device);
        var plan = new ReadPlan { Tags = tags };
        var values = (await driver.ReadAsync(device, plan)).ToDictionary(x => x.TagId);
        Assert.Equal(-2, values[1].Value); Assert.Equal(0x8001, values[2].Value); Assert.Equal(12.5, values[3].Value);
        Assert.Equal(1, values[4].Value); Assert.Equal(0, values[5].Value); Assert.Equal(1, values[6].Value); Assert.Equal(1, values[7].Value);
        Assert.Equal(5, driver.LastRequestCount); // D100～D103 合并一段、D200 因空隙 96 > 16 单独一段；M、X、W 各一段
        await driver.WriteAsync(device, tags[2], -7.25);
        await driver.WriteAsync(device, tags[3], 0);
        await driver.WriteAsync(device, tags[7], -100000);
        values = (await driver.ReadAsync(device, plan)).ToDictionary(x => x.TagId);
        Assert.Equal(-7.25, values[3].Value); Assert.Equal(0, values[4].Value); Assert.Equal(-100000, values[8].Value);
        Assert.Equal(unchecked((ushort)-100000), plc.Words["D"][200]); // 低字在前
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.WriteAsync(device, T(9, "W10.3", TagDataType.Bool, true), 1));
    }

    [Fact]
    public async Task EndCodeMarksOnlyThatSpanBadAndTimeoutDisconnects()
    {
        await using var plc = new McSimulator(McFormat.Binary, false);
        plc.Rejected.Add("R");
        var device = new Device { Id = 1, Name = "mc", Protocol = ProtocolType.MitsubishiMc, Host = "127.0.0.1", Port = plc.Port, TimeoutMs = 500 };
        await using var driver = new MitsubishiMcDriver();
        await driver.ConnectAsync(device);
        var plan = new ReadPlan { Tags = [T(1, "R0", TagDataType.Int16), T(2, "D0", TagDataType.Int16)] };
        var values = (await driver.ReadAsync(device, plan)).ToDictionary(x => x.TagId);
        Assert.Equal(TagQuality.Bad, values[1].Quality); Assert.Contains("C056", values[1].Error);
        Assert.Equal(TagQuality.Good, values[2].Quality);
        Assert.True(driver.IsConnected);
        plc.DropNext = 1;
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(device, plan));
        Assert.False(driver.IsConnected);
        await driver.ConnectAsync(device);
        Assert.Equal(TagQuality.Good, (await driver.ReadAsync(device, plan)).Single(x => x.TagId == 2).Quality);
    }
}
