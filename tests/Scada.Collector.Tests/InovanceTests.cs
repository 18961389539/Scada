using System.Net;
using System.Net.Sockets;
using Scada.Collector.Inovance;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.Tests;

public class InovanceTests
{
    [Theory]
    [InlineData(InovanceSeries.H3U, "D10", TagDataType.Int16, false, TagArea.HoldingRegister, 10, 1)]
    [InlineData(InovanceSeries.H3U, "d10", TagDataType.Float, false, TagArea.HoldingRegister, 10, 2)]
    [InlineData(InovanceSeries.H3U, "M10", TagDataType.Bool, false, TagArea.Coil, 10, 1)]
    [InlineData(InovanceSeries.H3U, "M8000", TagDataType.Bool, false, TagArea.Coil, 8000, 1)]
    [InlineData(InovanceSeries.H3U, "SM0", TagDataType.Bool, false, TagArea.Coil, 9216, 1)]
    [InlineData(InovanceSeries.H3U, "S10", TagDataType.Bool, false, TagArea.Coil, 57354, 1)]
    [InlineData(InovanceSeries.H3U, "T5", TagDataType.Bool, false, TagArea.Coil, 61445, 1)]
    [InlineData(InovanceSeries.H3U, "T5", TagDataType.Int16, false, TagArea.HoldingRegister, 61445, 1)]
    [InlineData(InovanceSeries.H3U, "C10", TagDataType.Int16, false, TagArea.HoldingRegister, 62474, 1)]
    [InlineData(InovanceSeries.H3U, "C200", TagDataType.Int32, false, TagArea.HoldingRegister, 63232, 2)]
    [InlineData(InovanceSeries.H3U, "C205", TagDataType.UInt32, false, TagArea.HoldingRegister, 63242, 2)]
    [InlineData(InovanceSeries.H3U, "X10", TagDataType.Bool, false, TagArea.Coil, 63496, 1)]
    [InlineData(InovanceSeries.H3U, "X377", TagDataType.Bool, false, TagArea.Coil, 63743, 1)]
    [InlineData(InovanceSeries.H3U, "Y0", TagDataType.Bool, true, TagArea.Coil, 64512, 1)]
    [InlineData(InovanceSeries.H3U, "R10", TagDataType.UInt16, false, TagArea.HoldingRegister, 12298, 1)]
    [InlineData(InovanceSeries.H3U, "SD0", TagDataType.Int16, false, TagArea.HoldingRegister, 9216, 1)]
    [InlineData(InovanceSeries.H5U, "B0", TagDataType.Bool, false, TagArea.Coil, 12288, 1)]
    [InlineData(InovanceSeries.H5U, "X1777", TagDataType.Bool, false, TagArea.Coil, 64511, 1)]
    [InlineData(InovanceSeries.H5U, "Y1777", TagDataType.Bool, true, TagArea.Coil, 65535, 1)]
    [InlineData(InovanceSeries.Easy, "D10", TagDataType.Int16, false, TagArea.HoldingRegister, 10, 1)]
    [InlineData(InovanceSeries.Easy, "R0", TagDataType.Int16, false, TagArea.HoldingRegister, 12288, 1)]
    [InlineData(InovanceSeries.AM, "MW10", TagDataType.Float, false, TagArea.HoldingRegister, 10, 2)]
    [InlineData(InovanceSeries.AM, "%MW0", TagDataType.Int16, false, TagArea.HoldingRegister, 0, 1)]
    [InlineData(InovanceSeries.AM, "MD1", TagDataType.Int32, false, TagArea.HoldingRegister, 2, 2)]
    [InlineData(InovanceSeries.AM, "QX1.0", TagDataType.Bool, true, TagArea.Coil, 8, 1)]
    [InlineData(InovanceSeries.AM, "QX8191.7", TagDataType.Bool, true, TagArea.Coil, 65535, 1)]
    [InlineData(InovanceSeries.AM, "IX0.7", TagDataType.Bool, false, TagArea.DiscreteInput, 7, 1)]
    [InlineData(InovanceSeries.AC, "MW10", TagDataType.Int16, false, TagArea.HoldingRegister, 10, 1)]
    [InlineData(InovanceSeries.AC, "IX1.0", TagDataType.Bool, false, TagArea.DiscreteInput, 8, 1)]
    public void SoftElementsConvertToModbusAddresses(InovanceSeries series, string text, TagDataType type, bool writable, TagArea area, int address, int length)
    {
        Assert.True(InovanceAddress.TryMap(series, text, type, writable, out var point, out var error), error);
        Assert.Equal(area, point.Area);
        Assert.Equal(address, point.Address);
        Assert.Equal(length, point.Length);
    }

    [Fact]
    public void WordBitIsTheRegisterBitAndIsReadOnly()
    {
        Assert.True(InovanceAddress.TryMap(InovanceSeries.H3U, "D10.3", TagDataType.Bool, false, out var point, out _));
        Assert.Equal(TagArea.HoldingRegister, point.Area);
        Assert.Equal(10, point.Address);
        Assert.Equal(3, point.Bit);
        Assert.True(InovanceAddress.TryMap(InovanceSeries.AM, "MX1.0", TagDataType.Bool, false, out var mx, out _));
        Assert.Equal(0, mx.Address);
        Assert.Equal(8, mx.Bit);
        Assert.False(InovanceAddress.TryMap(InovanceSeries.H3U, "D10.3", TagDataType.Bool, true, out _, out var writable));
        Assert.Contains("只读", writable);
    }

    [Theory]
    [InlineData(InovanceSeries.H3U, "M7680", TagDataType.Bool, false)]
    [InlineData(InovanceSeries.H3U, "X8", TagDataType.Bool, false)]
    [InlineData(InovanceSeries.H3U, "X10", TagDataType.Bool, true)]
    [InlineData(InovanceSeries.H3U, "W0", TagDataType.Int16, false)]
    [InlineData(InovanceSeries.H3U, "C200", TagDataType.Int16, false)]
    [InlineData(InovanceSeries.H3U, "T5", TagDataType.Float, false)]
    [InlineData(InovanceSeries.H3U, "D10", TagDataType.Bool, false)]
    [InlineData(InovanceSeries.H3U, "D8511", TagDataType.Float, false)]
    [InlineData(InovanceSeries.H5U, "M8000", TagDataType.Bool, false)]
    [InlineData(InovanceSeries.H5U, "T5", TagDataType.Bool, false)]
    [InlineData(InovanceSeries.H5U, "W0", TagDataType.Int16, false)]
    [InlineData(InovanceSeries.Easy, "SM0", TagDataType.Bool, false)]
    [InlineData(InovanceSeries.H3U, "D10", TagDataType.Double, false)]
    [InlineData(InovanceSeries.AM, "D10", TagDataType.Int16, false)]
    [InlineData(InovanceSeries.AM, "QX0.8", TagDataType.Bool, false)]
    [InlineData(InovanceSeries.AM, "IX0.0", TagDataType.Bool, true)]
    [InlineData(InovanceSeries.AM, "MD0", TagDataType.Int16, false)]
    [InlineData(InovanceSeries.AC, "SM0", TagDataType.Bool, false)]
    public void InvalidSoftElementsAreRejected(InovanceSeries series, string text, TagDataType type, bool writable)
    {
        Assert.False(InovanceAddress.TryMap(series, text, type, writable, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RtuRequiresASerialPortAndTcpRequiresAHost()
    {
        var tcp = Device("{\"series\":\"H5U\",\"transport\":\"Tcp\"}");
        Assert.Null(ConfigurationValidator.Device(tcp));
        var rtu = Device("{\"series\":\"Easy\",\"transport\":\"Rtu\"}");
        rtu.SerialPort = "";
        Assert.NotNull(ConfigurationValidator.Device(rtu));
        rtu.SerialPort = "COM3";
        rtu.StationNumber = 0;
        Assert.NotNull(ConfigurationValidator.Device(rtu));
        rtu.StationNumber = 1;
        Assert.Null(ConfigurationValidator.Device(rtu));
        var h3u = Device("{\"series\":\"H3U\",\"transport\":\"Tcp\"}");
        Assert.NotNull(ConfigurationValidator.Tag(new Tag { DeviceId = 1, Name = "x", Area = TagArea.Address, NodeId = "B1", DataType = TagDataType.Bool }, h3u));
        Assert.Null(ConfigurationValidator.Tag(new Tag { DeviceId = 1, Name = "b", Area = TagArea.Address, NodeId = "B1", DataType = TagDataType.Bool }, tcp));
        Assert.Null(ConfigurationValidator.Tag(new Tag { DeviceId = 1, Name = "m", Area = TagArea.Address, NodeId = "M10", DataType = TagDataType.Bool }, h3u));
    }

    [Fact]
    public void DefaultByteOrderBecomesLowWordFirstAndTcpCannotShareAChannel()
    {
        var device = Device("{\"series\":\"H3U\",\"transport\":\"Tcp\"}");
        device.ByteOrder = ByteOrder.ABCD;
        SoftElementByteOrder.Normalize(device);
        Assert.Equal(ByteOrder.CDAB, device.ByteOrder);
        device.ByteOrder = ByteOrder.BADC;
        SoftElementByteOrder.Normalize(device);
        Assert.Equal(ByteOrder.BADC, device.ByteOrder);

        device.ChannelId = 1;
        Assert.Contains("TCP", ConfigurationValidator.Device(device));
        var rtu = Device("{\"series\":\"H3U\",\"transport\":\"Rtu\"}");
        rtu.ChannelId = 1;
        Assert.Null(ConfigurationValidator.Device(rtu));
        Assert.True(ChannelProtocols.Supports(Scada.Core.Enums.ProtocolType.Inovance));
        Assert.True(ChannelProtocols.UsesSerial(Scada.Core.Enums.ProtocolType.Inovance));
        Assert.False(ChannelProtocols.UsesHostPort(Scada.Core.Enums.ProtocolType.Inovance));

        var node = "D{address}".Replace("{address}", "10");
        Assert.Null(ConfigurationValidator.Tag(new Tag { DeviceId = 1, Name = "d", Area = TagArea.Address, NodeId = node, DataType = TagDataType.Float }, rtu));
    }

    [Fact]
    public async Task TcpReadAndWriteUseTheConvertedAddress()
    {
        await using var server = new TinyModbusSlave();
        // 1.0f 的 CDAB 线上顺序是低字在前：D10=0x0000，D11=0x3F80。X10 是八进制，线圈地址 63496。
        server.Registers[10] = 0x0000;
        server.Registers[11] = 0x3F80;
        server.Registers[12] = 0x0001;
        server.Coils[63496] = true;
        var device = Device("{\"series\":\"H3U\",\"transport\":\"Tcp\",\"maxGap\":16}");
        device.Host = "127.0.0.1";
        device.Port = server.Port;
        device.ByteOrder = ByteOrder.CDAB;
        device.TimeoutMs = 1000;
        var tags = new[]
        {
            new Tag { Id = 1, Name = "d10", Area = TagArea.Address, NodeId = "D10", DataType = TagDataType.Float },
            new Tag { Id = 2, Name = "bit", Area = TagArea.Address, NodeId = "D12.0", DataType = TagDataType.Bool },
            new Tag { Id = 3, Name = "x10", Area = TagArea.Address, NodeId = "X10", DataType = TagDataType.Bool },
        };
        await using var driver = new InovanceDriver();
        await driver.ConnectAsync(device);
        var values = await driver.ReadAsync(device, new ReadPlan { DeviceId = 1, Tags = tags });
        Assert.Equal(1, values.Single(x => x.TagId == 1).Value, 3);
        Assert.Equal(1, values.Single(x => x.TagId == 2).Value);
        Assert.Equal(1, values.Single(x => x.TagId == 3).Value);

        var y = new Tag { Id = 4, Name = "y0", Area = TagArea.Address, NodeId = "Y0", DataType = TagDataType.Bool, Writable = true };
        await driver.WriteAsync(device, y, 1);
        Assert.True(server.Coils[64512]);
        Assert.True(driver.IsConnected);
    }

    [Fact]
    public async Task AmReadsInputBitsAndHoldingRegisters()
    {
        await using var server = new TinyModbusSlave();
        server.Registers[0] = 0x0000;
        server.Registers[1] = 0x3F80;
        server.Inputs[7] = true;
        var device = Device("{\"series\":\"AM\",\"transport\":\"Tcp\"}");
        device.Port = server.Port;
        device.ByteOrder = ByteOrder.CDAB;
        device.TimeoutMs = 1000;
        var tags = new[]
        {
            new Tag { Id = 1, Name = "mw", Area = TagArea.Address, NodeId = "MW0", DataType = TagDataType.Float },
            new Tag { Id = 2, Name = "ix", Area = TagArea.Address, NodeId = "IX0.7", DataType = TagDataType.Bool },
        };
        await using var driver = new InovanceDriver();
        await driver.ConnectAsync(device);
        var values = await driver.ReadAsync(device, new ReadPlan { DeviceId = 1, Tags = tags });
        Assert.Equal(1, values.Single(x => x.TagId == 1).Value, 3);
        Assert.Equal(1, values.Single(x => x.TagId == 2).Value);
        var qx = new Tag { Id = 3, Name = "qx", Area = TagArea.Address, NodeId = "QX1.0", DataType = TagDataType.Bool, Writable = true };
        await driver.WriteAsync(device, qx, 1);
        Assert.True(server.Coils[8]);
    }

    [Fact]
    public async Task SlaveRejectionNamesTheSoftElementAndModbusAddress()
    {
        await using var server = new TinyModbusSlave { RejectStart = 10 };
        server.Coils[0] = true;
        var device = Device("{\"series\":\"H3U\",\"transport\":\"Tcp\"}");
        device.Port = server.Port;
        device.TimeoutMs = 1000;
        var tags = new[]
        {
            new Tag { Id = 1, Name = "d10", Area = TagArea.Address, NodeId = "D10", DataType = TagDataType.Float },
            new Tag { Id = 2, Name = "m0", Area = TagArea.Address, NodeId = "M0", DataType = TagDataType.Bool },
        };
        await using var driver = new InovanceDriver();
        await driver.ConnectAsync(device);
        var values = await driver.ReadAsync(device, new ReadPlan { DeviceId = 1, Tags = tags });
        Assert.Equal("D10 → 保持寄存器 10～11：从站拒绝：非法数据地址", values.Single(x => x.TagId == 1).Error);
        Assert.Equal(TagQuality.Bad, values.Single(x => x.TagId == 1).Quality);
        Assert.Equal(1, values.Single(x => x.TagId == 2).Value);
        Assert.True(driver.IsConnected);
        Assert.Equal("X0 → 离散输入 1024：从站拒绝：非法数据地址", SoftElementFault.Rejected("X0", TagArea.DiscreteInput, 1024, 1, null, "从站拒绝：非法数据地址"));
        Assert.Equal("D10.3 → 保持寄存器 10 位 3：从站拒绝：非法数据地址", SoftElementFault.Rejected("D10.3", TagArea.HoldingRegister, 10, 1, 3, "从站拒绝：非法数据地址"));
    }

    [Fact]
    public async Task InvalidOperationsAndConnectionFailuresAreReported()
    {
        var device = Device("{\"series\":\"H3U\",\"transport\":\"Tcp\"}");
        await using var driver = new InovanceDriver();
        var tag = new Tag { Id = 1, Name = "d10", Area = TagArea.Address, NodeId = "D10", DataType = TagDataType.Int16 };
        var writable = new Tag { Id = 2, Name = "d11", Area = TagArea.Address, NodeId = "D11", DataType = TagDataType.Int16, Writable = true };

        var notConnectedRead = await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(device, new ReadPlan { DeviceId = 1, Tags = [tag] }));
        Assert.Contains("未连接", notConnectedRead.Message);
        var notConnectedWrite = await Assert.ThrowsAsync<IOException>(() => driver.WriteAsync(device, writable, 1));
        Assert.Contains("未连接", notConnectedWrite.Message);

        await using var server = new TinyModbusSlave();
        device.Port = server.Port;
        device.TimeoutMs = 1000;
        await driver.ConnectAsync(device);
        Assert.True(driver.IsConnected);

        var readOnly = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.WriteAsync(device, tag, 1));
        Assert.Contains("只读", readOnly.Message);
        var invalid = new Tag { Id = 3, Name = "bad", Area = TagArea.Address, NodeId = "NOPE", DataType = TagDataType.Int16, Writable = true };
        var badAddress = await Assert.ThrowsAsync<InvalidOperationException>(() => driver.WriteAsync(device, invalid, 1));
        Assert.False(string.IsNullOrWhiteSpace(badAddress.Message));

        driver.Disconnect();
        Assert.False(driver.IsConnected);
    }

    [Fact]
    public async Task TcpConnectionFailureAndEmptyRtuPortAreNamed()
    {
        var tcp = Device("{\"series\":\"H3U\",\"transport\":\"Tcp\"}");
        tcp.Port = 1;
        await using var driver = new InovanceDriver();
        var failure = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(tcp));
        Assert.Contains("汇川 Modbus TCP 连接失败", failure.Message);
        Assert.False(driver.IsConnected);

        var rtu = Device("{\"series\":\"H3U\",\"transport\":\"Rtu\"}");
        rtu.SerialPort = "";
        var serial = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(rtu));
        Assert.Contains("未配置串口名称", serial.Message);
    }

    private static Device Device(string options) => new()
    {
        Id = 1, Name = "inovance", Protocol = Scada.Core.Enums.ProtocolType.Inovance, Host = "127.0.0.1", Port = 502,
        StationNumber = 1, SerialPort = "COM1", BaudRate = 9600, DataBits = 8, ProtocolOptions = options
    };

    private sealed class TinyModbusSlave : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        public ushort[] Registers { get; } = new ushort[65536];
        public bool[] Coils { get; } = new bool[65536];
        public bool[] Inputs { get; } = new bool[65536];
        public int? RejectStart { get; set; }
        public int Port { get; }

        public TinyModbusSlave()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptAsync);
        }

        private async Task AcceptAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var header = new byte[6];
            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header)) return;
                var length = (header[4] << 8) | header[5];
                var body = new byte[length];
                if (!await ReadExactAsync(stream, body)) return;
                var pdu = body.AsSpan(1);
                var function = pdu[0];
                var start = (pdu[1] << 8) | pdu[2];
                var count = (pdu[3] << 8) | pdu[4];
                byte[] payload = RejectStart is int rejected && function is 1 or 2 or 3 or 4 && start == rejected
                    ? [(byte)(function | 0x80), 2]
                    : function switch
                {
                    1 => BitsResponse(1, Coils, start, count),
                    2 => BitsResponse(2, Inputs, start, count),
                    3 => RegistersResponse(start, count),
                    5 => WriteCoil(start, pdu[3], pdu[4]),
                    16 => WriteRegisters(start, count, pdu[5..]),
                    _ => [ (byte)(function | 0x80), 1 ]
                };
                var response = new byte[7 + payload.Length];
                header.AsSpan(0, 4).CopyTo(response);
                response[4] = (byte)((payload.Length + 1) >> 8);
                response[5] = (byte)(payload.Length + 1);
                response[6] = body[0];
                payload.CopyTo(response.AsSpan(7));
                await stream.WriteAsync(response);
            }
        }

        private static byte[] BitsResponse(byte function, bool[] bits, int start, int count)
        {
            var packed = new byte[(count + 7) / 8];
            for (var i = 0; i < count; i++) if (bits[start + i]) packed[i / 8] |= (byte)(1 << (i % 8));
            var payload = new byte[2 + packed.Length];
            payload[0] = function;
            payload[1] = (byte)packed.Length;
            packed.CopyTo(payload.AsSpan(2));
            return payload;
        }

        private byte[] RegistersResponse(int start, int count)
        {
            var payload = new byte[2 + count * 2];
            payload[0] = 3;
            payload[1] = (byte)(count * 2);
            for (var i = 0; i < count; i++)
            {
                payload[2 + i * 2] = (byte)(Registers[start + i] >> 8);
                payload[3 + i * 2] = (byte)Registers[start + i];
            }
            return payload;
        }

        private byte[] WriteCoil(int address, byte hi, byte lo)
        {
            Coils[address] = hi == 0xFF;
            return [5, (byte)(address >> 8), (byte)address, hi, lo];
        }

        private byte[] WriteRegisters(int start, int count, ReadOnlySpan<byte> data)
        {
            for (var i = 0; i < count; i++) Registers[start + i] = (ushort)((data[1 + i * 2] << 8) | data[2 + i * 2]);
            return [16, (byte)(start >> 8), (byte)start, (byte)(count >> 8), (byte)count];
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer)
        {
            var filled = 0;
            while (filled < buffer.Length)
            {
                int read;
                try { read = await stream.ReadAsync(buffer.AsMemory(filled)); }
                catch (Exception) { return false; }
                if (read == 0) return false;
                filled += read;
            }
            return true;
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
