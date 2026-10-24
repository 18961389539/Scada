using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Collector.Modbus;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

public class ModbusTcpDriverTests
{
    [Fact]
    public async Task CoilWritesUseTheEchoedAddressAndReadBackBothStates()
    {
        await using var server = new FakeModbusServer(8); var device = Device(server.Port); var tag = CoilTag(1, 2); tag.Writable = true;
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance); await driver.ConnectAsync(device);
        var plan = new ReadPlanBuilder().Build(device, [tag]);
        await driver.WriteAsync(device, tag, 1); Assert.Equal(1, Assert.Single(await driver.ReadAsync(device, plan)).Value);
        await driver.WriteAsync(device, tag, 0); Assert.Equal(0, Assert.Single(await driver.ReadAsync(device, plan)).Value);
        Assert.Equal(2, server.Writes);
    }
    [Theory]
    [InlineData("transaction")] [InlineData("protocol")] [InlineData("unit")] [InlineData("function")] [InlineData("length")] [InlineData("byteCount")]
    public async Task MalformedResponsesAreRejectedAndTheConnectionIsClosed(string field)
    {
        await using var server = new FakeModbusServer(8);
        server.Mutate = bytes => { bytes[field switch { "transaction" => 1, "protocol" => 3, "unit" => 6, "function" => 7, "length" => 4, _ => 8 }]++; return bytes; };
        var device = Device(server.Port); await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);
        await driver.ConnectAsync(device);
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(device, new ReadPlanBuilder().Build(device, [Tag(1, 0)])));
        Assert.False(driver.IsConnected);
    }
    [Fact]
    public async Task AWriteWithALostAcknowledgementIsSentExactlyOnce()
    {
        await using var server = new FakeModbusServer(8) { DropWriteAck = true }; var device = Device(server.Port);
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance); await driver.ConnectAsync(device);
        await Assert.ThrowsAnyAsync<IOException>(() => driver.WriteAsync(device, Tag(1, 0, true), 12.5));
        Assert.Equal(1, server.Writes); Assert.Equal(12.5f, server.ReadFloat(0)); Assert.False(driver.IsConnected);
    }
    [Fact]
    public async Task WrongWriteEchoIsUnknownRatherThanSuccessAndIsNeverRetried()
    {
        await using var server = new FakeModbusServer(8) { Mutate = bytes => { bytes[9]++; return bytes; } }; var device = Device(server.Port);
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance); await driver.ConnectAsync(device);
        await Assert.ThrowsAsync<IOException>(() => driver.WriteAsync(device, Tag(1, 0, true), 12.5));
        Assert.Equal(1, server.Writes); Assert.False(driver.IsConnected);
    }
    [Fact]
    public async Task ACancelledWriteNeverEntersTheWireAndDoesNotDamageTheConnection()
    {
        await using var server = new FakeModbusServer(8); var device = Device(server.Port);
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance); await driver.ConnectAsync(device);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.WriteAsync(device, Tag(1, 0, true), 12.5, cancel.Token));
        Assert.Equal(0, server.Writes); Assert.True(driver.IsConnected);
    }
    [Fact]
    public async Task ReadAsync_DecodesHoldingRegistersFromWireOrder()
    {
        await using var server = new FakeModbusServer(registerCount: 32);
        server.WriteFloat(0, 103.5f);
        server.WriteFloat(2, 1.75f);

        var device = Device(server.Port);
        var tags = new[]
        {
            Tag(1, 0), Tag(2, 2)
        };
        var plan = new ReadPlanBuilder().Build(device, tags);
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);

        await ConnectAsync(driver, device);
        var values = await driver.ReadAsync(device, plan);

        Assert.True(driver.IsConnected);
        Assert.Equal(103.5, values.Single(x => x.TagId == 1).Value, 3);
        Assert.Equal(1.75, values.Single(x => x.TagId == 2).Value, 3);
    }

    [Fact]
    public async Task ReadAsync_DecodesCoils()
    {
        await using var server = new FakeModbusServer(registerCount: 32);
        server.SetCoil(1, true);
        server.SetCoil(3, true);

        var device = Device(server.Port);
        var tags = new[] { CoilTag(1, 0), CoilTag(2, 1), CoilTag(3, 3) };
        var plan = new ReadPlanBuilder().Build(device, tags);
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);

        await ConnectAsync(driver, device);
        var values = await driver.ReadAsync(device, plan);

        Assert.Equal(0, values.Single(x => x.TagId == 1).Value);
        Assert.Equal(1, values.Single(x => x.TagId == 2).Value);
        Assert.Equal(1, values.Single(x => x.TagId == 3).Value);
    }

    [Fact]
    public async Task WriteAsync_SendsBigEndianFloatAndReadsBack()
    {
        await using var server = new FakeModbusServer(registerCount: 32);
        var device = Device(server.Port);
        var tag = Tag(1, 5, writable: true);
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);

        await ConnectAsync(driver, device);
        await driver.WriteAsync(device, tag, 42.5);

        Assert.Equal(42.5f, server.ReadFloat(5));
        var plan = new ReadPlanBuilder().Build(device, new[] { tag });
        var values = await driver.ReadAsync(device, plan);
        Assert.Equal(42.5, values.Single().Value, 3);
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenServerClosesConnection()
    {
        await using var server = new FakeModbusServer(registerCount: 8);
        var device = Device(server.Port);
        var plan = new ReadPlanBuilder().Build(device, new[] { Tag(1, 0) });
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);

        await ConnectAsync(driver, device);
        server.Shutdown();

        await Assert.ThrowsAnyAsync<Exception>(() => driver.ReadAsync(device, plan));
        Assert.False(driver.IsConnected);
    }

    private static async Task ConnectAsync(ModbusTcpDriver driver, Device device)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await driver.ConnectAsync(device);
                return;
            }
            catch (Exception) when (attempt < 4)
            {
                await Task.Delay(100);
            }
        }
    }

    private static Device Device(int port) => new()
    {
        Id = 1,
        Name = "test",
        Host = "127.0.0.1",
        Port = port,
        StationNumber = 1,
        ByteOrder = ByteOrder.ABCD,
        TimeoutMs = 2000,
        PollIntervalMs = 1000,
        Enabled = true
    };

    private static Tag Tag(int id, int address, bool writable = false) => new()
    {
        Id = id,
        DeviceId = 1,
        Name = $"float{id}",
        Area = TagArea.HoldingRegister,
        Address = address,
        DataType = TagDataType.Float,
        Writable = writable
    };

    private static Tag CoilTag(int id, int address) => new()
    {
        Id = id,
        DeviceId = 1,
        Name = $"coil{id}",
        Area = TagArea.Coil,
        Address = address,
        DataType = TagDataType.Bool
    };

    private sealed class FakeModbusServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _clients = new();
        private readonly ushort[] _registers;
        private readonly bool[] _coils;
        private readonly Task _acceptLoop;

        public FakeModbusServer(int registerCount)
        {
            _registers = new ushort[registerCount];
            _coils = new bool[registerCount * 2];
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }
        public Func<byte[], byte[]>? Mutate;
        public bool DropWriteAck;
        private int _writes;
        public int Writes => Volatile.Read(ref _writes);

        public void WriteFloat(int address, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            _registers[address] = (ushort)((bytes[0] << 8) | bytes[1]);
            _registers[address + 1] = (ushort)((bytes[2] << 8) | bytes[3]);
        }

        public float ReadFloat(int address)
        {
            var bytes = new[]
            {
                (byte)(_registers[address] >> 8), (byte)(_registers[address] & 0xFF),
                (byte)(_registers[address + 1] >> 8), (byte)(_registers[address + 1] & 0xFF)
            };
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToSingle(bytes);
        }

        public void SetCoil(int address, bool value) => _coils[address] = value;

        public void Shutdown()
        {
            _listener.Stop();
            CloseClients();
        }

        public void CloseClients()
        {
            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    client.Close();
                }

                _clients.Clear();
            }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    lock (_clients)
                    {
                        _clients.Add(client);
                    }

                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = new byte[7];
                try
                {
                    while (true)
                    {
                        if (!await ReadExactAsync(stream, header, 7))
                        {
                            return;
                        }

                        var length = (header[4] << 8) | header[5];
                        var pdu = new byte[length - 1];
                        if (!await ReadExactAsync(stream, pdu, pdu.Length))
                        {
                            return;
                        }

                        var response = BuildResponse(pdu);
                        if (pdu[0] == 16 && DropWriteAck) return;
                        var adu = new byte[7 + response.Length];
                        adu[0] = header[0];
                        adu[1] = header[1];
                        adu[4] = (byte)((response.Length + 1) >> 8);
                        adu[5] = (byte)((response.Length + 1) & 0xFF);
                        adu[6] = header[6];
                        Buffer.BlockCopy(response, 0, adu, 7, response.Length);
                        if (Mutate is not null) adu = Mutate(adu);
                        await stream.WriteAsync(adu, _cts.Token);
                    }
                }
                catch (Exception) when (!_cts.IsCancellationRequested)
                {
                }
            }
        }

        private byte[] BuildResponse(byte[] pdu)
        {
            var function = pdu[0];
            var address = (pdu[1] << 8) | pdu[2];
            var quantity = (pdu[3] << 8) | pdu[4];

            if (function == 3)
            {
                var response = new byte[2 + quantity * 2];
                response[0] = 3;
                response[1] = (byte)(quantity * 2);
                for (var i = 0; i < quantity; i++)
                {
                    var register = _registers[address + i];
                    response[2 + i * 2] = (byte)(register >> 8);
                    response[3 + i * 2] = (byte)(register & 0xFF);
                }

                return response;
            }

            if (function == 1)
            {
                var byteCount = (quantity + 7) / 8;
                var response = new byte[2 + byteCount];
                response[0] = 1;
                response[1] = (byte)byteCount;
                for (var i = 0; i < quantity; i++)
                {
                    if (_coils[address + i])
                    {
                        response[2 + i / 8] |= (byte)(1 << (i % 8));
                    }
                }

                return response;
            }

            if (function == 5)
            {
                Interlocked.Increment(ref _writes); _coils[address] = quantity == 0xff00; return pdu[..5];
            }
            if (function == 16)
            {
                Interlocked.Increment(ref _writes);
                for (var i = 0; i < quantity; i++)
                {
                    _registers[address + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
                }

                return pdu[..5];
            }

            return [(byte)(function | 0x80), 0x01];
        }

        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
                if (read == 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            CloseClients();
            try
            {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // ignored
            }

            _cts.Dispose();
        }
    }
}
