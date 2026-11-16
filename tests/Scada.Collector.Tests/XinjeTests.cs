using System.Net;
using System.Net.Sockets;
using Scada.Collector.Xinje;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.Tests;

public class XinjeTests
{
    [Theory]
    [InlineData(XinjeSeries.XC, "D10", TagDataType.Int16, TagArea.HoldingRegister, 10, 1)]
    [InlineData(XinjeSeries.XC, "D8000", TagDataType.Int16, TagArea.HoldingRegister, 16384, 1)]
    [InlineData(XinjeSeries.XC, "M8000", TagDataType.Bool, TagArea.Coil, 24576, 1)]
    [InlineData(XinjeSeries.XC, "X10", TagDataType.Bool, TagArea.Coil, 16392, 1)]
    [InlineData(XinjeSeries.XC, "X1037", TagDataType.Bool, TagArea.Coil, 16927, 1)]
    [InlineData(XinjeSeries.XC, "Y0", TagDataType.Bool, TagArea.Coil, 18432, 1)]
    [InlineData(XinjeSeries.XC, "T0", TagDataType.Bool, TagArea.Coil, 25600, 1)]
    [InlineData(XinjeSeries.XC, "TD0", TagDataType.Int16, TagArea.HoldingRegister, 12288, 1)]
    [InlineData(XinjeSeries.XC, "FD0", TagDataType.Int16, TagArea.HoldingRegister, 18432, 1)]
    [InlineData(XinjeSeries.XD, "D10", TagDataType.Float, TagArea.HoldingRegister, 10, 2)]
    [InlineData(XinjeSeries.XD, "D8000", TagDataType.Int16, TagArea.HoldingRegister, 8000, 1)]
    [InlineData(XinjeSeries.XD, "M8000", TagDataType.Bool, TagArea.Coil, 8000, 1)]
    [InlineData(XinjeSeries.XD, "X10", TagDataType.Bool, TagArea.Coil, 20488, 1)]
    [InlineData(XinjeSeries.XD, "X77", TagDataType.Bool, TagArea.Coil, 20543, 1)]
    [InlineData(XinjeSeries.XD, "X10000", TagDataType.Bool, TagArea.Coil, 20736, 1)]
    [InlineData(XinjeSeries.XD, "X11777", TagDataType.Bool, TagArea.Coil, 21759, 1)]
    [InlineData(XinjeSeries.XD, "Y0", TagDataType.Bool, TagArea.Coil, 24576, 1)]
    [InlineData(XinjeSeries.XD, "SM0", TagDataType.Bool, TagArea.Coil, 36864, 1)]
    [InlineData(XinjeSeries.XD, "T0", TagDataType.Bool, TagArea.Coil, 40960, 1)]
    [InlineData(XinjeSeries.XD, "TD0", TagDataType.UInt16, TagArea.HoldingRegister, 32768, 1)]
    [InlineData(XinjeSeries.XD, "ID0", TagDataType.Int16, TagArea.HoldingRegister, 20480, 1)]
    [InlineData(XinjeSeries.XD, "ID10000", TagDataType.Int16, TagArea.HoldingRegister, 20736, 1)]
    [InlineData(XinjeSeries.XD, "ID11599", TagDataType.Int16, TagArea.HoldingRegister, 22335, 1)]
    [InlineData(XinjeSeries.XL, "D10", TagDataType.Int16, TagArea.HoldingRegister, 10, 1)]
    [InlineData(XinjeSeries.XL, "X0", TagDataType.Bool, TagArea.Coil, 20480, 1)]
    [InlineData(XinjeSeries.XL, "QD0", TagDataType.Int16, TagArea.HoldingRegister, 24576, 1)]
    public void SoftElementsConvertToModbusAddresses(XinjeSeries series, string text, TagDataType type, TagArea area, int address, int length)
    {
        Assert.True(XinjeAddress.TryMap(series, text, type, false, out var point, out var error), error);
        Assert.Equal(area, point.Area);
        Assert.Equal(address, point.Address);
        Assert.Equal(length, point.Length);
    }

    [Fact]
    public void WordBitIsReadOnlyAndTimerValueUsesTd()
    {
        Assert.True(XinjeAddress.TryMap(XinjeSeries.XC, "D10.3", TagDataType.Bool, false, out var point, out _));
        Assert.Equal(10, point.Address);
        Assert.Equal(3, point.Bit);
        Assert.False(XinjeAddress.TryMap(XinjeSeries.XC, "D10.3", TagDataType.Bool, true, out _, out var writable));
        Assert.Contains("只读", writable);
        Assert.False(XinjeAddress.TryMap(XinjeSeries.XC, "T5", TagDataType.Int16, false, out _, out var timer));
        Assert.Contains("TD", timer);
    }

    [Theory]
    [InlineData(XinjeSeries.XC, "SM0", TagDataType.Bool)]
    [InlineData(XinjeSeries.XC, "X8", TagDataType.Bool)]
    [InlineData(XinjeSeries.XC, "ID0", TagDataType.Int16)]
    [InlineData(XinjeSeries.XD, "ED0", TagDataType.Int16)]
    [InlineData(XinjeSeries.XD, "X100", TagDataType.Bool)]
    [InlineData(XinjeSeries.XD, "X10008", TagDataType.Bool)]
    [InlineData(XinjeSeries.XL, "M30000", TagDataType.Bool)]
    [InlineData(XinjeSeries.XC, "D10", TagDataType.Double)]
    public void InvalidSoftElementsAreRejected(XinjeSeries series, string text, TagDataType type)
    {
        Assert.False(XinjeAddress.TryMap(series, text, type, false, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void RtuRequiresASerialPortAndSeriesSelectsTheMap()
    {
        var xc = Device("{\"series\":\"XC\",\"transport\":\"Tcp\"}");
        Assert.Null(ConfigurationValidator.Device(xc));
        Assert.Null(ConfigurationValidator.Tag(new Tag { DeviceId = 1, Name = "m", Area = TagArea.Address, NodeId = "M10", DataType = TagDataType.Bool }, xc));
        Assert.NotNull(ConfigurationValidator.Tag(new Tag { DeviceId = 1, Name = "sm", Area = TagArea.Address, NodeId = "SM0", DataType = TagDataType.Bool }, xc));
        var rtu = Device("{\"series\":\"XL\",\"transport\":\"Rtu\"}");
        rtu.SerialPort = "";
        Assert.NotNull(ConfigurationValidator.Device(rtu));
        rtu.SerialPort = "COM4";
        rtu.StationNumber = 1;
        Assert.Null(ConfigurationValidator.Device(rtu));
        Assert.Null(ConfigurationValidator.Tag(new Tag { DeviceId = 1, Name = "sm", Area = TagArea.Address, NodeId = "SM0", DataType = TagDataType.Bool }, rtu));
    }

    [Fact]
    public async Task TcpReadAndWriteUseTheXcMap()
    {
        await using var server = new TinyModbusSlave();
        server.Registers[10] = 0x0000;
        server.Registers[11] = 0x3F80;
        server.Coils[16392] = true;
        var device = Device("{\"series\":\"XC\",\"transport\":\"Tcp\"}");
        device.Port = server.Port;
        device.ByteOrder = ByteOrder.CDAB;
        device.TimeoutMs = 1000;
        var tags = new[]
        {
            new Tag { Id = 1, Name = "d10", Area = TagArea.Address, NodeId = "D10", DataType = TagDataType.Float },
            new Tag { Id = 2, Name = "x10", Area = TagArea.Address, NodeId = "X10", DataType = TagDataType.Bool },
        };
        await using var driver = new XinjeDriver();
        await driver.ConnectAsync(device);
        var values = await driver.ReadAsync(device, new ReadPlan { DeviceId = 1, Tags = tags });
        Assert.Equal(1, values.Single(x => x.TagId == 1).Value, 3);
        Assert.Equal(1, values.Single(x => x.TagId == 2).Value);
        var y = new Tag { Id = 3, Name = "y0", Area = TagArea.Address, NodeId = "Y0", DataType = TagDataType.Bool, Writable = true };
        await driver.WriteAsync(device, y, 1);
        Assert.True(server.Coils[18432]);
    }

    [Fact]
    public async Task InvalidOperationsAndConnectionFailuresAreReported()
    {
        var device = Device("{\"series\":\"XC\",\"transport\":\"Tcp\"}");
        await using var driver = new XinjeDriver();
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
        var tcp = Device("{\"series\":\"XC\",\"transport\":\"Tcp\"}");
        tcp.Port = 1;
        await using var driver = new XinjeDriver();
        var failure = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(tcp));
        Assert.Contains("信捷 Modbus TCP 连接失败", failure.Message);
        Assert.False(driver.IsConnected);

        var rtu = Device("{\"series\":\"XL\",\"transport\":\"Rtu\"}");
        rtu.SerialPort = "";
        var serial = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(rtu));
        Assert.Contains("未配置串口名称", serial.Message);
    }

    private static Device Device(string options) => new()
    {
        Id = 1, Name = "xinje", Protocol = Scada.Core.Enums.ProtocolType.Xinje, Host = "127.0.0.1", Port = 502,
        StationNumber = 1, SerialPort = "COM1", BaudRate = 9600, DataBits = 8, ProtocolOptions = options
    };

    private sealed class TinyModbusSlave : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public ushort[] Registers { get; } = new ushort[65536];
        public bool[] Coils { get; } = new bool[65536];
        public int Port { get; }

        public TinyModbusSlave()
        {
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
                catch (Exception) { return; }
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
                byte[] payload = function switch
                {
                    1 => PackCoils(start, (pdu[3] << 8) | pdu[4]),
                    3 => PackRegisters(start, (pdu[3] << 8) | pdu[4]),
                    5 => WriteCoil(start, pdu[3], pdu[4]),
                    _ => [(byte)(function | 0x80), 1]
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

        private byte[] PackCoils(int start, int count)
        {
            var packed = new byte[(count + 7) / 8];
            for (var i = 0; i < count; i++) if (Coils[start + i]) packed[i / 8] |= (byte)(1 << (i % 8));
            var payload = new byte[2 + packed.Length];
            payload[0] = 1;
            payload[1] = (byte)packed.Length;
            packed.CopyTo(payload.AsSpan(2));
            return payload;
        }

        private byte[] PackRegisters(int start, int count)
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
