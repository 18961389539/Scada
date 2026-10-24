using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Scada.Collector.S7;
using Scada.Core.Entities;
using Scada.Core.Enums;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Collector.Tests;

public class S7IntegrationTests
{
    [Fact]
    public async Task S7WireHandshakeGroupedReadsAndNativeBitWritesPreserveNeighbours()
    {
        await using var plc = new PlcFixture();
        var device = new Device { Id = 1, Name = "s7-test", Protocol = ProtocolType.SiemensS7, Host = "127.0.0.1", Port = plc.Port, TimeoutMs = 2000, Rack = 1, Slot = 2 };
        var real = new Tag { Id = 1, DeviceId = 1, Name = "real", Area = TagArea.DataBlock, DbNumber = 12, Address = 1, DataType = TagDataType.Float, Writable = true };
        var bit = new Tag { Id = 2, DeviceId = 1, Name = "bit", Area = TagArea.DataBlock, DbNumber = 12, Address = 5, BitOffset = 2, DataType = TagDataType.Bool, Writable = true };
        BinaryPrimitives.WriteSingleBigEndian(plc.Data(0x84, 12).AsSpan(1), 12.5f); plc.Data(0x84, 12)[5] = 0b10101010;
        await using var driver = new SiemensS7Driver(); await driver.ConnectAsync(device);
        Assert.True(driver.IsConnected); Assert.Equal(1 * 32 + 2, plc.RemoteTsap);
        var plan = new ReadPlanBuilder().Build(device, [real, bit]);
        var values = await driver.ReadAsync(device, plan);
        Assert.Equal(12.5, values[0].Value); Assert.Equal(0, values[1].Value); Assert.Equal(1, plc.ReadCount);
        await driver.WriteAsync(device, bit, 1); Assert.Equal(0b10101110, plc.Data(0x84, 12)[5]);
        Assert.Equal(1, plc.LastWriteTransport); // Native S7 bit request, not read-modify-write.
        await driver.WriteAsync(device, real, 25.25); Assert.Equal(25.25f, BinaryPrimitives.ReadSingleBigEndian(plc.Data(0x84, 12).AsSpan(1)));
        values = await driver.ReadAsync(device, plan); Assert.Equal(25.25, values[0].Value); Assert.Equal(1, values[1].Value);
        var memory = real.Clone(); memory.Id = 3; memory.Area = TagArea.Memory; memory.Address = 3; memory.DataType = TagDataType.Int16;
        await driver.WriteAsync(device, memory, -123); values = await driver.ReadAsync(device, new ReadPlanBuilder().Build(device, [memory]));
        Assert.Equal(-123, values[0].Value);
    }

    [Fact]
    public async Task S7RejectsPlcErrorsAndTimedOutReadsCanReconnect()
    {
        await using var plc = new PlcFixture();
        var device = new Device { Id = 1, Name = "s7-test", Protocol = ProtocolType.SiemensS7, Host = "127.0.0.1", Port = plc.Port, TimeoutMs = 2000 };
        var tag = new Tag { Id = 1, DeviceId = 1, Name = "real", Area = TagArea.DataBlock, DataType = TagDataType.Float };
        var plan = new ReadPlanBuilder().Build(device, [tag]);
        await using var driver = new SiemensS7Driver(); await driver.ConnectAsync(device);
        plc.FailNextRead = true;
        var rejected = await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(device, plan));
        Assert.Contains("Object does not exist", rejected.Message); Assert.False(driver.IsConnected); Assert.False(plc.FailNextRead);
        await driver.ConnectAsync(device); plc.DropNextRead = true;
        var shortRead = device.Clone(); shortRead.TimeoutMs = 500;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => driver.ReadAsync(shortRead, plan)); Assert.False(driver.IsConnected);
        Assert.False(plc.DropNextRead); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        await driver.ConnectAsync(device); Assert.Single(await driver.ReadAsync(device, plan)); Assert.True(driver.IsConnected);
    }

    // Small wire-level PLC fixture: real TCP, TPKT/COTP setup and S7 read/write PDUs.
    // It intentionally fragments responses and checks the client's requested areas/offsets.
    private sealed class PlcFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly ConcurrentDictionary<(byte, int), byte[]> _areas = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int ReadCount; public int LastWriteTransport; public int RemoteTsap;
        public bool FailNextRead; public bool DropNextRead;
        public byte[] Data(byte area, int db) => _areas.GetOrAdd((area, db), _ => new byte[65536]);
        public PlcFixture() { _listener.Start(); _accept = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) _clients.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_stop.Token))); }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream(); var request = await PacketAsync(stream);
                    RemoteTsap = request[18];
                    var cotp = request.ToArray(); cotp[5] = 0xd0; await stream.WriteAsync(cotp, _stop.Token);
                    request = await PacketAsync(stream);
                    await SendAsync(stream, [0x32, 3, 0, 0, request[11], request[12], 0, 8, 0, 0, 0, 0, 0xf0, 0, 0, 1, 0, 1, 0, 240]);
                    while (!_stop.IsCancellationRequested)
                    {
                        request = await PacketAsync(stream); var s7 = request[7..];
                        var function = s7[10]; var transport = s7[15]; var count = BinaryPrimitives.ReadUInt16BigEndian(s7.AsSpan(16));
                        var db = BinaryPrimitives.ReadUInt16BigEndian(s7.AsSpan(18)); var area = s7[20];
                        var bitAddress = s7[21] * 65536 + s7[22] * 256 + s7[23]; var offset = bitAddress / 8;
                        var data = Data(area, db); var reference = s7[4..6];
                        if (function == 4)
                        {
                            Interlocked.Increment(ref ReadCount);
                            if (DropNextRead) { DropNextRead = false; continue; }
                            var code = FailNextRead ? (byte)0x0a : (byte)0xff; FailNextRead = false;
                            var length = count + 4;
                            var response = new byte[18 + count];
                            new byte[] { 0x32, 3, 0, 0, reference[0], reference[1], 0, 2, (byte)(length >> 8), (byte)length, 0, 0, 4, 1, code, 4, (byte)(count * 8 >> 8), (byte)(count * 8) }.CopyTo(response, 0);
                            data.AsSpan(offset, count).CopyTo(response.AsSpan(18)); await SendAsync(stream, response);
                        }
                        else if (function == 5)
                        {
                            LastWriteTransport = transport;
                            if (transport == 1) { var mask = (byte)(1 << (bitAddress % 8)); data[offset] = s7[28] == 0 ? (byte)(data[offset] & ~mask) : (byte)(data[offset] | mask); }
                            else s7.AsSpan(28, count).CopyTo(data.AsSpan(offset));
                            await SendAsync(stream, [0x32, 3, 0, 0, reference[0], reference[1], 0, 2, 0, 1, 0, 0, 5, 1, 0xff]);
                        }
                        else throw new IOException("Unexpected fixture function");
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
            }
        }
        private async Task<byte[]> PacketAsync(NetworkStream stream)
        {
            var header = new byte[4]; await stream.ReadExactlyAsync(header, _stop.Token);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)); var bytes = new byte[length]; header.CopyTo(bytes, 0);
            await stream.ReadExactlyAsync(bytes.AsMemory(4), _stop.Token); return bytes;
        }
        private async Task SendAsync(NetworkStream stream, byte[] payload)
        {
            var bytes = new byte[7 + payload.Length]; bytes[0] = 3; BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)bytes.Length);
            bytes[4] = 2; bytes[5] = 0xf0; bytes[6] = 0x80; payload.CopyTo(bytes, 7);
            await stream.WriteAsync(bytes.AsMemory(0, 3), _stop.Token); await stream.WriteAsync(bytes.AsMemory(3), _stop.Token);
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_clients); _stop.Dispose(); }
    }
}
