using System.Buffers.Binary;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector.Modbus;

public sealed class ModbusRtuDriver(SerialBusPool buses) : IDeviceDriver
{
    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }
    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        try { await buses.ExecuteAsync(device, _ => true, cancellationToken); IsConnected = true; LastError = null; }
        catch (Exception ex) { IsConnected = false; LastError = ex.Message; throw; }
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        var result = new List<RawTagValue>();
        foreach (var block in plan.Blocks)
        {
            try
            {
                var bytes = await buses.ExecuteAsync(device, port => RtuFrame.Read(port, device.StationNumber,
                    ModbusValueCodec.FunctionCode(block.Area), block.StartAddress, block.Length), cancellationToken);
                foreach (var item in block.Tags)
                {
                    var value = block.Area is TagArea.Coil or TagArea.DiscreteInput
                        ? (ModbusValueCodec.DecodeCoil(bytes, item.Offset) ? 1 : 0)
                        : ModbusValueCodec.DecodeRegister(bytes, item.Offset, item.DataType, item.ByteOrder);
                    if (!double.IsFinite(value)) throw new IOException($"测点 #{item.TagId} 返回非有限值");
                    result.Add(new(item.TagId, value));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { IsConnected = false; LastError = $"{block.Area} 地址 {block.StartAddress}，长度 {block.Length}：{ex.Message}"; throw new IOException(LastError, ex); }
        }
        IsConnected = true; LastError = null;
        return result;
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable || tag.Area is not (TagArea.Coil or TagArea.HoldingRegister)) throw new InvalidOperationException("测点只读");
        try
        {
            await buses.ExecuteAsync(device, port =>
            {
                if (tag.Area == TagArea.Coil) RtuFrame.WriteCoil(port, device.StationNumber, tag.Address, value != 0);
                else RtuFrame.WriteRegisters(port, device.StationNumber, tag.Address, ModbusValueCodec.EncodeRegister(value, tag.DataType, device.ByteOrder));
                return true;
            }, cancellationToken);
            LastError = null;
        }
        catch (Exception ex) { IsConnected = false; LastError = ex.Message; throw; }
    }

    public void Disconnect() => IsConnected = false;
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}

public static class RtuFrame
{
    public static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xffff;
        foreach (var value in bytes)
        { crc ^= value; for (var i = 0; i < 8; i++) crc = (ushort)((crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xa001)); }
        return crc;
    }

    public static byte[] Read(ISerialConnection port, byte station, byte function, int address, int length)
    {
        if (station is < 1 or > 247 || function is < 1 or > 4 || address is < 0 or > 65535 || length < 1
            || length > (function <= 2 ? 2000 : 125) || (long)address + length > 65536) throw new ArgumentOutOfRangeException(nameof(length));
        var request = new byte[6]; request[0] = station; request[1] = function;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)length);
        port.Write(AppendCrc(request));
        var header = ReadExactly(port, 3);
        CheckException(port, header, station, function);
        var expected = function <= 2 ? (length + 7) / 8 : length * 2;
        if (header[2] != expected) throw new IOException($"RTU 返回字节数 {header[2]}，预期 {expected}");
        var tail = ReadExactly(port, expected + 2);
        Verify([.. header, .. tail]);
        return tail[..expected];
    }

    public static void WriteCoil(ISerialConnection port, byte station, int address, bool value)
    {
        var request = new byte[6]; request[0] = station; request[1] = 5;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), checked((ushort)address));
        request[4] = value ? (byte)0xff : (byte)0;
        Write(port, request);
    }

    public static void WriteRegisters(ISerialConnection port, byte station, int address, byte[] bytes)
    {
        if (bytes.Length is < 2 or > 246 || bytes.Length % 2 != 0 || address < 0 || (long)address + bytes.Length / 2 > 65536)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        var request = new byte[7 + bytes.Length]; request[0] = station; request[1] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)(bytes.Length / 2));
        request[6] = (byte)bytes.Length; bytes.CopyTo(request, 7);
        Write(port, request);
    }

    private static void Write(ISerialConnection port, byte[] request)
    {
        if (request[0] is < 1 or > 247) throw new ArgumentOutOfRangeException(nameof(request), "不支持 RTU 广播写入");
        port.Write(AppendCrc(request));
        var header = ReadExactly(port, 3); CheckException(port, header, request[0], request[1]);
        var response = header.Concat(ReadExactly(port, 5)).ToArray(); Verify(response);
        if (!response.AsSpan(0, 6).SequenceEqual(request.AsSpan(0, 6))) throw new IOException("RTU 写入响应地址或值/数量不匹配");
    }

    private static void CheckException(ISerialConnection port, byte[] header, byte station, byte function)
    {
        if (header[0] != station) throw new IOException("RTU 响应站号不匹配");
        if (header[1] == (function | 0x80))
        { Verify([.. header, .. ReadExactly(port, 2)]); throw new IOException($"RTU 从站异常 0x{header[2]:X2}"); }
        if (header[1] != function) throw new IOException("RTU 响应功能码不匹配");
    }

    public static byte[] AppendCrc(ReadOnlySpan<byte> payload)
    { var bytes = new byte[payload.Length + 2]; payload.CopyTo(bytes); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(payload.Length), Crc(payload)); return bytes; }
    private static void Verify(byte[] bytes)
    { if (Crc(bytes.AsSpan(0, bytes.Length - 2)) != BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2))) throw new IOException("RTU CRC 校验失败"); }
    private static byte[] ReadExactly(ISerialConnection port, int count)
    {
        var bytes = new byte[count]; var offset = 0;
        while (offset < count) { var received = port.Read(bytes, offset, count - offset); if (received <= 0) throw new EndOfStreamException("RTU 响应不完整"); offset += received; }
        return bytes;
    }
}
