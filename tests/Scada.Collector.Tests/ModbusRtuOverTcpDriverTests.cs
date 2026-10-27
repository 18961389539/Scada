using System.Net;
using System.Net.Sockets;
using ProtocolType = Scada.Core.Enums.ProtocolType;
using Scada.Collector.Modbus;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

// NModbus 替换自研帧层后，RTU 与 ASCII 都能跑在任意 IStreamResource 上；
// 这里用 TCP 承载一个最小 RTU 从站，验证 RTU over TCP 驱动的读取（站号 + 字节序）与写入回读。
public class ModbusRtuOverTcpDriverTests
{
    [Fact]
    public async Task ReadsAndWritesRegistersOverTcpUsingStationAndByteOrder()
    {
        await using var slave = new RtuSlave();
        var device = new Device
        {
            Id = 1, Protocol = ProtocolType.ModbusRtuOverTcp, Host = "127.0.0.1", Port = slave.Port,
            StationNumber = 4, ByteOrder = ByteOrder.BADC, TimeoutMs = 3000
        };
        var tag = new Tag { Id = 9, DeviceId = 1, Name = "word", DataType = TagDataType.UInt16, Address = 10, Writable = true };
        await using var driver = new ModbusRtuOverTcpDriver();
        await driver.ConnectAsync(device);

        // 从站寄存器 10 的线缆值是大端 0x1234；BADC 字节序解析后为 0x3412。
        var values = await driver.ReadAsync(device, new ReadPlanBuilder().Build(device, [tag]));
        Assert.Equal(0x3412, Assert.Single(values).Value);
        Assert.True(driver.IsConnected);

        // 写 0x1234：按 BADC 编码后线缆值为 0x3412（字节 0x34 0x12）。
        await driver.WriteAsync(device, tag, 0x1234);
        Assert.Equal(new ushort[] { 0x3412 }, slave.Written);
    }
}

// 最小 RTU 从站：只处理"读保持寄存器"与"写多个寄存器"，用于在无串口环境下验证 RTU over TCP 路径。
internal sealed class RtuSlave : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ushort[] _registers = new ushort[128];
    private readonly Task _loop;

    public RtuSlave()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _registers[10] = 0x1234;
        _loop = Task.Run(ServeAsync);
    }

    public int Port { get; }
    public ushort[] Written { get; private set; } = [];

    private async Task ServeAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            var stream = client.GetStream();

            var read = await ReadExactlyAsync(stream, 8);
            if (Crc(read.AsSpan(0, 6)) != (ushort)(read[6] | (read[7] << 8))) return;
            var station = read[0];
            var function = read[1];
            var start = (ushort)((read[2] << 8) | read[3]);
            var count = (ushort)((read[4] << 8) | read[5]);
            var body = new byte[3 + count * 2];
            body[0] = station; body[1] = function; body[2] = (byte)(count * 2);
            for (var i = 0; i < count; i++)
            { var value = _registers[start + i]; body[3 + i * 2] = (byte)(value >> 8); body[4 + i * 2] = (byte)value; }
            await stream.WriteAsync(Append(body), _cts.Token);

            var head = await ReadExactlyAsync(stream, 7);
            var byteCount = head[6];
            var tail = await ReadExactlyAsync(stream, byteCount + 2);
            var writeStart = (ushort)((head[2] << 8) | head[3]);
            var writeCount = (ushort)((head[4] << 8) | head[5]);
            var written = new ushort[writeCount];
            for (var i = 0; i < writeCount; i++) written[i] = (ushort)((tail[i * 2] << 8) | tail[i * 2 + 1]);
            Written = written;
            for (var i = 0; i < writeCount; i++) _registers[writeStart + i] = written[i];
            await stream.WriteAsync(Append(head.AsSpan(0, 6).ToArray()), _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 测试结束时的断开忽略 */ }
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count]; var offset = 0;
        while (offset < count) { var read = await stream.ReadAsync(buffer.AsMemory(offset)); if (read <= 0) throw new EndOfStreamException(); offset += read; }
        return buffer;
    }

    private static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xffff;
        foreach (var value in bytes) { crc ^= value; for (var i = 0; i < 8; i++) crc = (ushort)((crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xa001)); }
        return crc;
    }

    private static byte[] Append(ReadOnlySpan<byte> payload)
    {
        var crc = Crc(payload); var frame = new byte[payload.Length + 2];
        payload.CopyTo(frame); frame[payload.Length] = (byte)crc; frame[payload.Length + 1] = (byte)(crc >> 8);
        return frame;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch { /* 忽略关闭期异常 */ }
        _cts.Dispose();
    }
}