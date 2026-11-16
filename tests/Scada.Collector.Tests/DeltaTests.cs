using System.Net;
using System.Net.Sockets;
using Scada.Collector.Delta;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector.Tests;

public class DeltaTests
{
    [Theory]
    [InlineData(DeltaSeries.DVP, "D10", TagDataType.Float, false, TagArea.HoldingRegister, 4106, 2)]
    [InlineData(DeltaSeries.DVP, "D4096", TagDataType.Int16, false, TagArea.HoldingRegister, 36864, 1)]
    [InlineData(DeltaSeries.DVP, "D9999", TagDataType.Int16, false, TagArea.HoldingRegister, 42767, 1)]
    [InlineData(DeltaSeries.DVP, "M0", TagDataType.Bool, false, TagArea.Coil, 2048, 1)]
    [InlineData(DeltaSeries.DVP, "M1536", TagDataType.Bool, false, TagArea.Coil, 45056, 1)]
    [InlineData(DeltaSeries.DVP, "X10", TagDataType.Bool, false, TagArea.DiscreteInput, 1032, 1)]
    [InlineData(DeltaSeries.DVP, "Y0", TagDataType.Bool, true, TagArea.Coil, 1280, 1)]
    [InlineData(DeltaSeries.DVP, "T5", TagDataType.Bool, false, TagArea.Coil, 1541, 1)]
    [InlineData(DeltaSeries.DVP, "T5", TagDataType.Int16, false, TagArea.HoldingRegister, 1541, 1)]
    [InlineData(DeltaSeries.DVP, "C10", TagDataType.Int16, false, TagArea.HoldingRegister, 3594, 1)]
    [InlineData(DeltaSeries.DVP, "C200", TagDataType.Int32, false, TagArea.HoldingRegister, 3784, 2)]
    [InlineData(DeltaSeries.AS, "D10", TagDataType.Int16, false, TagArea.HoldingRegister, 10, 1)]
    [InlineData(DeltaSeries.AS, "M100", TagDataType.Bool, false, TagArea.Coil, 100, 1)]
    [InlineData(DeltaSeries.AS, "X0.0", TagDataType.Bool, false, TagArea.DiscreteInput, 24576, 1)]
    [InlineData(DeltaSeries.AS, "X10.0", TagDataType.Bool, false, TagArea.DiscreteInput, 24736, 1)]
    [InlineData(DeltaSeries.AS, "X0", TagDataType.UInt16, false, TagArea.InputRegister, 32768, 1)]
    [InlineData(DeltaSeries.AS, "Y0.0", TagDataType.Bool, true, TagArea.Coil, 40960, 1)]
    [InlineData(DeltaSeries.AS, "SM0", TagDataType.Bool, false, TagArea.Coil, 16384, 1)]
    [InlineData(DeltaSeries.AS, "SR0", TagDataType.Int16, false, TagArea.HoldingRegister, 49152, 1)]
    [InlineData(DeltaSeries.AS, "HC0", TagDataType.Int32, false, TagArea.HoldingRegister, 64512, 2)]
    public void SoftElementsConvertToModbusAddresses(DeltaSeries series, string text, TagDataType type, bool writable, TagArea area, int address, int length)
    {
        Assert.True(DeltaAddress.TryMap(series, text, type, writable, out var point, out var error), error);
        Assert.Equal(area, point.Area);
        Assert.Equal(address, point.Address);
        Assert.Equal(length, point.Length);
    }

    [Theory]
    [InlineData(DeltaSeries.DVP, "X8", TagDataType.Bool, false)]
    [InlineData(DeltaSeries.DVP, "X10", TagDataType.Bool, true)]
    [InlineData(DeltaSeries.DVP, "SM0", TagDataType.Bool, false)]
    [InlineData(DeltaSeries.DVP, "C200", TagDataType.Int16, false)]
    [InlineData(DeltaSeries.DVP, "D10", TagDataType.Bool, false)]
    [InlineData(DeltaSeries.AS, "X0", TagDataType.Bool, false)]
    [InlineData(DeltaSeries.AS, "X0.0", TagDataType.Bool, true)]
    [InlineData(DeltaSeries.AS, "D30000", TagDataType.Int16, false)]
    [InlineData(DeltaSeries.AS, "E0", TagDataType.Double, false)]
    public void InvalidSoftElementsAreRejected(DeltaSeries series, string text, TagDataType type, bool writable)
    {
        Assert.False(DeltaAddress.TryMap(series, text, type, writable, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task TcpReadUsesDiscreteInputAndHoldingRegister()
    {
        await using var server = new TinyModbusSlave();
        server.Registers[4106] = 0x0000;
        server.Registers[4107] = 0x3F80;
        server.Inputs[1032] = true;
        var device = Device("{\"series\":\"DVP\",\"transport\":\"Tcp\"}");
        device.Port = server.Port;
        device.ByteOrder = ByteOrder.CDAB;
        device.TimeoutMs = 1000;
        var tags = new[]
        {
            new Tag { Id = 1, Name = "d10", Area = TagArea.Address, NodeId = "D10", DataType = TagDataType.Float },
            new Tag { Id = 2, Name = "x10", Area = TagArea.Address, NodeId = "X10", DataType = TagDataType.Bool },
        };
        await using var driver = new DeltaDriver();
        await driver.ConnectAsync(device);
        var values = await driver.ReadAsync(device, new ReadPlan { DeviceId = 1, Tags = tags });
        Assert.Equal(1, values.Single(x => x.TagId == 1).Value, 3);
        Assert.Equal(1, values.Single(x => x.TagId == 2).Value);
        var y = new Tag { Id = 3, Name = "y0", Area = TagArea.Address, NodeId = "Y0", DataType = TagDataType.Bool, Writable = true };
        await driver.WriteAsync(device, y, 1);
        Assert.True(server.Coils[1280]);
    }

    [Fact]
    public async Task InvalidOperationsAndConnectionFailuresAreReported()
    {
        var device = Device("{\"series\":\"DVP\",\"transport\":\"Tcp\"}");
        await using var driver = new DeltaDriver();
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
        var tcp = Device("{\"series\":\"DVP\",\"transport\":\"Tcp\"}");
        tcp.Port = 1;
        await using var driver = new DeltaDriver();
        var failure = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(tcp));
        Assert.Contains("台达 Modbus TCP 连接失败", failure.Message);
        Assert.False(driver.IsConnected);

        var rtu = Device("{\"series\":\"DVP\",\"transport\":\"Rtu\"}");
        rtu.SerialPort = "";
        var serial = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(rtu));
        Assert.Contains("未配置串口名称", serial.Message);
    }

    private static Device Device(string options) => new()
    {
        Id = 1, Name = "delta", Protocol = Scada.Core.Enums.ProtocolType.Delta, Host = "127.0.0.1", Port = 502,
        StationNumber = 1, SerialPort = "COM1", BaudRate = 9600, DataBits = 8, ProtocolOptions = options
    };

    private sealed class TinyModbusSlave : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public ushort[] Registers { get; } = new ushort[65536];
        public bool[] Coils { get; } = new bool[65536];
        public bool[] Inputs { get; } = new bool[65536];
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
                    1 => PackBits(1, Coils, start, (pdu[3] << 8) | pdu[4]),
                    2 => PackBits(2, Inputs, start, (pdu[3] << 8) | pdu[4]),
                    3 => PackRegisters(3, start, (pdu[3] << 8) | pdu[4]),
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

        private static byte[] PackBits(byte function, bool[] bits, int start, int count)
        {
            var packed = new byte[(count + 7) / 8];
            for (var i = 0; i < count; i++) if (bits[start + i]) packed[i / 8] |= (byte)(1 << (i % 8));
            var payload = new byte[2 + packed.Length];
            payload[0] = function;
            payload[1] = (byte)packed.Length;
            packed.CopyTo(payload.AsSpan(2));
            return payload;
        }

        private byte[] PackRegisters(byte function, int start, int count)
        {
            var payload = new byte[2 + count * 2];
            payload[0] = function;
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
