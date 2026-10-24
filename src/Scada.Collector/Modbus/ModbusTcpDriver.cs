using System.Buffers.Binary;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector.Modbus;

// One request per exchange. A lost write acknowledgement must never trigger a physical retry.
public sealed class ModbusTcpDriver(ILogger<ModbusTcpDriver> logger) : IDeviceDriver
{
    private TcpClient? _client;
    private readonly SemaphoreSlim _wire = new(1, 1);
    private ushort _transaction;
    public bool IsConnected => _client?.Connected == true;
    public string? LastError { get; private set; }

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        await _wire.WaitAsync(cancellationToken);
        var client = new TcpClient { NoDelay = true };
        try
        {
            Disconnect(); using var deadline = Deadline(device, cancellationToken);
            await client.ConnectAsync(device.Host, device.Port, deadline.Token); deadline.Token.ThrowIfCancellationRequested();
            _client = client; LastError = null;
        }
        catch (Exception ex)
        { client.Dispose(); LastError = ex.Message; logger.LogWarning("Modbus TCP 连接失败：{Host}:{Port}", device.Host, device.Port); throw; }
        finally { _wire.Release(); }
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        var values = new List<RawTagValue>();
        foreach (var block in plan.Blocks)
        {
            var function = ModbusValueCodec.FunctionCode(block.Area);
            if (block.StartAddress is < 0 or > 65535 || block.Length < 1 || block.Length > (function <= 2 ? 2000 : 125)
                || (long)block.StartAddress + block.Length > 65536) throw new ArgumentException("Modbus TCP 读取范围无效");
            var request = new byte[5]; request[0] = function;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(1), (ushort)block.StartAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), (ushort)block.Length);
            var response = await ExchangeAsync(device, request, cancellationToken);
            var expected = function <= 2 ? (block.Length + 7) / 8 : block.Length * 2;
            if (response.Length != expected + 2 || response[1] != expected) { Disconnect(); throw new IOException("Modbus TCP 返回字节数不匹配"); }
            var bytes = response.AsSpan(2);
            foreach (var tag in block.Tags)
            {
                var value = function <= 2 ? ModbusValueCodec.DecodeCoil(bytes, tag.Offset) ? 1d : 0d
                    : ModbusValueCodec.DecodeRegister(bytes, tag.Offset, tag.DataType, tag.ByteOrder);
                values.Add(double.IsFinite(value) ? new(tag.TagId, value) : new(tag.TagId, 0, TagQuality.Bad, Error: "设备返回非有限数值"));
            }
        }
        return values;
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable || tag.Area is not (TagArea.Coil or TagArea.HoldingRegister) || tag.Address is < 0 or > 65535)
            throw new ArgumentException("测点只读或 Modbus TCP 写地址无效");
        byte[] request;
        if (tag.Area == TagArea.Coil)
        { request = new byte[5]; request[0] = 5; BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(1), (ushort)tag.Address); request[3] = value != 0 ? (byte)255 : (byte)0; }
        else
        {
            var bytes = ModbusValueCodec.EncodeRegister(value, tag.DataType, device.ByteOrder);
            if ((long)tag.Address + bytes.Length / 2 > 65536) throw new ArgumentException("Modbus TCP 写范围超限");
            request = new byte[6 + bytes.Length]; request[0] = 16;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(1), (ushort)tag.Address);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), (ushort)(bytes.Length / 2)); request[5] = (byte)bytes.Length; bytes.CopyTo(request, 6);
        }
        var response = await ExchangeAsync(device, request, cancellationToken);
        if (response.Length != 5 || !response.AsSpan().SequenceEqual(request.AsSpan(0, 5)))
        { Disconnect(); throw new IOException("Modbus TCP 写响应地址或值/数量不匹配，执行结果须核查"); }
    }

    private async Task<byte[]> ExchangeAsync(Device device, byte[] request, CancellationToken ct)
    {
        using var deadline = Deadline(device, ct); var entered = false; TcpClient? client = null;
        try
        {
            await _wire.WaitAsync(deadline.Token); entered = true; client = _client ?? throw new IOException("Modbus TCP 未连接");
            var transaction = unchecked(++_transaction); var frame = new byte[7 + request.Length];
            BinaryPrimitives.WriteUInt16BigEndian(frame, transaction); BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(1 + request.Length));
            frame[6] = device.StationNumber; request.CopyTo(frame, 7);
            var stream = client.GetStream(); deadline.Token.ThrowIfCancellationRequested();
            await stream.WriteAsync(frame, deadline.Token);
            var header = new byte[7]; await stream.ReadExactlyAsync(header, deadline.Token);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (BinaryPrimitives.ReadUInt16BigEndian(header) != transaction || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0
                || header[6] != device.StationNumber || length is < 2 or > 254) throw new IOException("Modbus TCP 响应事务号、协议、站号或长度无效");
            var response = new byte[length - 1]; await stream.ReadExactlyAsync(response, deadline.Token);
            if (response[0] == (request[0] | 0x80) && response.Length == 2) throw new IOException($"Modbus TCP 从站异常 0x{response[1]:X2}");
            if (response[0] != request[0]) throw new IOException("Modbus TCP 响应功能码不匹配");
            LastError = null; return response;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        { if (client is not null) Close(client); LastError = "Modbus TCP 操作超时"; throw new TimeoutException(LastError, ex); }
        catch (Exception ex) { if (client is not null) Close(client); LastError = ex.Message; throw; }
        finally { if (entered) _wire.Release(); }
    }
    private static CancellationTokenSource Deadline(Device device, CancellationToken ct)
    { var source = CancellationTokenSource.CreateLinkedTokenSource(ct); source.CancelAfter(Math.Max(100, device.TimeoutMs)); return source; }
    private void Close(TcpClient client) { Interlocked.CompareExchange(ref _client, null, client); client.Dispose(); }
    public void Disconnect() => Interlocked.Exchange(ref _client, null)?.Dispose();
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
