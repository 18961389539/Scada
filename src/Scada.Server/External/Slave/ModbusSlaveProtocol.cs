using System.Buffers.Binary;
using Scada.Protocols.Modbus;
using Scada.Core.Enums;
using Scada.Core.Options;

namespace Scada.Server.External.Slave;

/// <summary>
/// Modbus 从站 PDU 处理。只做三件事：地址校验、值编解码、把写请求交给统一控制闭环。
/// 帧层（MBAP）与连接管理在 <see cref="ModbusSlaveHost"/>；值层复用采集侧的 ModbusValueCodec，
/// 保证"采进来的 float 与送出去的 float"用同一套字节序规则。
/// </summary>
public static class ModbusSlaveProtocol
{
    public const byte ReadCoils = 1, ReadDiscreteInputs = 2, ReadHoldingRegisters = 3, ReadInputRegisters = 4;
    public const byte WriteSingleCoil = 5, WriteSingleRegister = 6, WriteMultipleCoils = 15, WriteMultipleRegisters = 16;
    public const byte ReadWriteMultipleRegisters = 23;

    public const byte IllegalFunction = 0x01, IllegalDataAddress = 0x02, IllegalDataValue = 0x03, ServerDeviceFailure = 0x04;

    // 一次多线圈/多寄存器写最多触发多少个控制点。控制下发是串行的现场动作，
    // 放开到 2000 会让一个报文占用控制链路几十秒，也会把一次误操作放大成批量动作。
    private const int MaxWritePoints = 64;

    public static async Task<byte[]> HandleAsync(ReadOnlyMemory<byte> pdu, ModbusSlaveContext ctx, CancellationToken ct)
    {
        if (pdu.Length < 1) return Exception(0, IllegalFunction);
        var function = pdu.Span[0];
        try
        {
            return function switch
            {
                ReadCoils => ReadBits(pdu.Span, ctx, TagArea.Coil),
                ReadDiscreteInputs => ReadBits(pdu.Span, ctx, TagArea.DiscreteInput),
                ReadHoldingRegisters => ReadRegisters(pdu.Span, ctx, TagArea.HoldingRegister),
                ReadInputRegisters => ReadRegisters(pdu.Span, ctx, TagArea.InputRegister),
                WriteSingleCoil => await WriteCoilAsync(pdu.ToArray(), ctx, ct),
                WriteSingleRegister => await WriteRegisterAsync(pdu.ToArray(), ctx, ct),
                WriteMultipleCoils => await WriteCoilsAsync(pdu.ToArray(), ctx, ct),
                WriteMultipleRegisters => await WriteRegistersAsync(pdu.ToArray(), ctx, ct),
                ReadWriteMultipleRegisters => await ReadWriteAsync(pdu.ToArray(), ctx, ct),
                _ => Exception(function, IllegalFunction)
            };
        }
        catch (ModbusRequestException ex)
        {
            return Exception(function, ex.Code);
        }
    }

    public static byte[] Exception(byte function, byte code) => [(byte)(function | 0x80), code];

    // ---- 读 ----

    private static byte[] ReadBits(ReadOnlySpan<byte> pdu, ModbusSlaveContext ctx, TagArea area)
    {
        RequireLength(pdu, 5);
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (quantity is < 1 || quantity > Math.Clamp(ctx.Options.MaxReadBits, 1, 2000))
            throw new ModbusRequestException(IllegalDataValue);

        var table = ctx.Map.Modbus.Table(area);
        var bytes = new byte[(quantity + 7) / 8];
        for (var i = 0; i < quantity; i++)
        {
            if (!table.TryGetValue(start + i, out var slot) || !slot.IsBit) continue;
            var sample = ctx.Map.Read(slot, ctx.Options.BadQualityValue);
            if (sample.Value != 0) bytes[i / 8] |= (byte)(1 << (i % 8));
        }
        return PackBits(pdu[0], bytes);
    }

    private static byte[] ReadRegisters(ReadOnlySpan<byte> pdu, ModbusSlaveContext ctx, TagArea area)
    {
        var (start, quantity) = ReadRange(pdu, ctx);
        return PackRegisters(pdu[0], EncodeRange(ctx, area, start, quantity));
    }

    // ---- 写 ----

    private static async Task<byte[]> WriteCoilAsync(byte[] pdu, ModbusSlaveContext ctx, CancellationToken ct)
    {
        RequireLength(pdu, 5);
        EnsureWritable(ctx);
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var raw = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        if (raw is not (0xFF00 or 0x0000)) throw new ModbusRequestException(IllegalDataValue);
        if (!ctx.Map.Modbus.Table(TagArea.Coil).TryGetValue(address, out var slot))
            throw new ModbusRequestException(IllegalDataAddress);
        await ApplyAsync(ctx, slot, raw == 0xFF00 ? 1d : 0d, ct);
        return pdu[..5];
    }

    private static async Task<byte[]> WriteRegisterAsync(byte[] pdu, ModbusSlaveContext ctx, CancellationToken ct)
    {
        RequireLength(pdu, 5);
        EnsureWritable(ctx);
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        if (!ctx.Map.Modbus.Table(TagArea.HoldingRegister).TryGetValue(address, out var slot))
            throw new ModbusRequestException(IllegalDataAddress);
        // 单寄存器写只对"一个寄存器就能表达完整值"的类型成立。对 Float/Double 写半个值等于写脏数据。
        if (slot.RegisterCount != 1)
            throw new ModbusRequestException(IllegalDataValue);
        var value = ModbusValueCodec.DecodeRegister(pdu.AsSpan(3, 2), 0, slot.DataType, ctx.Options.ByteOrder);
        await ApplyAsync(ctx, slot, value, ct);
        return pdu[..5];
    }

    private static async Task<byte[]> WriteCoilsAsync(byte[] pdu, ModbusSlaveContext ctx, CancellationToken ct)
    {
        EnsureWritable(ctx);
        RequireLength(pdu, 6);
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        if (quantity is < 1 || quantity > Math.Clamp(ctx.Options.MaxReadBits, 1, 2000))
            throw new ModbusRequestException(IllegalDataValue);
        var byteCount = pdu[5];
        if (byteCount != (quantity + 7) / 8 || pdu.Length < 6 + byteCount)
            throw new ModbusRequestException(IllegalDataValue);
        if (quantity > MaxWritePoints) throw new ModbusRequestException(IllegalDataValue);

        var table = ctx.Map.Modbus.Table(TagArea.Coil);
        var plan = new List<(ExportPointMap.ExportSlot Slot, double Value)>(quantity);
        for (var i = 0; i < quantity; i++)
        {
            if (!table.TryGetValue(start + i, out var slot) || !slot.IsBit)
                throw new ModbusRequestException(IllegalDataAddress);
            var on = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
            plan.Add((slot, on ? 1d : 0d));
        }
        foreach (var (slot, value) in plan) await ApplyAsync(ctx, slot, value, ct);
        return pdu[..5];
    }

    private static async Task<byte[]> WriteRegistersAsync(byte[] pdu, ModbusSlaveContext ctx, CancellationToken ct)
    {
        EnsureWritable(ctx);
        var plan = BuildRegisterWrites(pdu, ctx);
        foreach (var (slot, value) in plan) await ApplyAsync(ctx, slot, value, ct);
        return pdu[..5];
    }

    // FC23：先读后写。写失败会让整个报文失败（Modbus 语义如此），因此读值在写之前取。
    private static async Task<byte[]> ReadWriteAsync(byte[] pdu, ModbusSlaveContext ctx, CancellationToken ct)
    {
        EnsureWritable(ctx);
        RequireLength(pdu, 10);
        var readStart = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var readQuantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        if (readQuantity is < 1 || readQuantity > Math.Clamp(ctx.Options.MaxReadRegisters, 1, 125))
            throw new ModbusRequestException(IllegalDataValue);
        var plan = BuildRegisterWrites(pdu, ctx, 5);
        var read = EncodeRange(ctx, TagArea.HoldingRegister, readStart, readQuantity);
        foreach (var (slot, value) in plan) await ApplyAsync(ctx, slot, value, ct);
        return PackRegisters(ReadWriteMultipleRegisters, read);
    }

    // 把 [起始地址, 数量) 拆成"每个测点一次写入"，并校验不能从多寄存器点的中间开始写。
    // writeStartOffset 是起始地址字段在 PDU 中的绝对下标：FC16 为 1，FC23 因为前面有读区间而为 5。
    private static List<(ExportPointMap.ExportSlot Slot, double Value)> BuildRegisterWrites(byte[] pdu, ModbusSlaveContext ctx, int writeStartOffset = 1)
    {
        var writeStart = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(writeStartOffset));
        var writeQuantity = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(writeStartOffset + 2));
        var byteCountOffset = writeStartOffset + 4;
        if (pdu.Length <= byteCountOffset) throw new ModbusRequestException(IllegalDataValue);
        var byteCount = pdu[byteCountOffset];
        if (writeQuantity is < 1 || writeQuantity > Math.Clamp(ctx.Options.MaxReadRegisters, 1, 125))
            throw new ModbusRequestException(IllegalDataValue);
        if (byteCount != writeQuantity * 2 || pdu.Length < byteCountOffset + 1 + byteCount)
            throw new ModbusRequestException(IllegalDataValue);
        if (writeQuantity > MaxWritePoints) throw new ModbusRequestException(IllegalDataValue);

        var table = ctx.Map.Modbus.Table(TagArea.HoldingRegister);
        var data = pdu.AsSpan(byteCountOffset + 1, byteCount);
        var plan = new List<(ExportPointMap.ExportSlot Slot, double Value)>();
        var address = (int)writeStart;
        var consumed = 0;
        while (consumed < writeQuantity)
        {
            if (!table.TryGetValue(address, out var slot)) throw new ModbusRequestException(IllegalDataAddress);
            if (address != slot.Address) throw new ModbusRequestException(IllegalDataAddress);
            if (slot.RegisterCount > writeQuantity - consumed) throw new ModbusRequestException(IllegalDataValue);
            var value = ModbusValueCodec.DecodeRegister(data.Slice(consumed * 2, slot.RegisterCount * 2), 0, slot.DataType, ctx.Options.ByteOrder);
            plan.Add((slot, value));
            address += slot.RegisterCount;
            consumed += slot.RegisterCount;
        }
        return plan;
    }

    // ---- 公共 ----

    private static async Task ApplyAsync(ModbusSlaveContext ctx, ExportPointMap.ExportSlot slot, double value, CancellationToken ct)
    {
        if (!slot.Writable || !double.IsFinite(value)) throw new ModbusRequestException(IllegalDataValue);
        var result = await ctx.Router.WriteAsync(slot.TagId, value, "外部 Modbus 主站", ctx.RemoteIp, ct);
        // Modbus 没有"受理但未完成"的语义：控制链路拒绝或回读失败都必须让主站看到异常，
        // 否则上位机会以为写入成功，形成静默的控制偏差。
        if (!result.Success) throw new ModbusRequestException(ServerDeviceFailure);
    }

    private static ushort[] EncodeRange(ModbusSlaveContext ctx, TagArea area, int start, int quantity)
    {
        var table = ctx.Map.Modbus.Table(area);
        var registers = new ushort[quantity];
        var cache = new Dictionary<ExportPointMap.ExportSlot, ushort[]>();
        for (var i = 0; i < quantity; i++)
        {
            if (!table.TryGetValue(start + i, out var slot)) continue;
            if (!cache.TryGetValue(slot, out var words))
            {
                var sample = ctx.Map.Read(slot, ctx.Options.BadQualityValue);
                var bytes = ModbusValueCodec.EncodeRegister(sample.Value, slot.DataType, ctx.Options.ByteOrder);
                words = ToWords(bytes);
                cache[slot] = words;
            }
            var offset = start + i - slot.Address;
            if (offset >= 0 && offset < words.Length) registers[i] = words[offset];
        }
        return registers;
    }

    private static (int Start, int Quantity) ReadRange(ReadOnlySpan<byte> pdu, ModbusSlaveContext ctx)
    {
        RequireLength(pdu, 5);
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        var quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        if (quantity is < 1 || quantity > Math.Clamp(ctx.Options.MaxReadRegisters, 1, 125))
            throw new ModbusRequestException(IllegalDataValue);
        return (start, quantity);
    }

    private static void EnsureWritable(ModbusSlaveContext ctx)
    {
        if (!ctx.Options.AllowWrite) throw new ModbusRequestException(IllegalFunction);
    }

    private static void RequireLength(ReadOnlySpan<byte> pdu, int length)
    {
        if (pdu.Length < length) throw new ModbusRequestException(IllegalDataValue);
    }

    private static byte[] PackBits(byte function, byte[] bytes)
    {
        var response = new byte[2 + bytes.Length];
        response[0] = function;
        response[1] = (byte)bytes.Length;
        bytes.CopyTo(response, 2);
        return response;
    }

    private static byte[] PackRegisters(byte function, ushort[] registers)
    {
        var response = new byte[2 + registers.Length * 2];
        response[0] = function;
        response[1] = (byte)(registers.Length * 2);
        for (var i = 0; i < registers.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + i * 2), registers[i]);
        return response;
    }

    private static ushort[] ToWords(byte[] bytes)
    {
        var words = new ushort[bytes.Length / 2];
        for (var i = 0; i < words.Length; i++) words[i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        return words;
    }
}

public sealed record ModbusSlaveContext(
    ExportPointMap Map,
    ModbusSlaveOptions Options,
    ExportWriteRouter Router,
    string RemoteIp);

public sealed class ModbusRequestException(byte code) : Exception
{
    public byte Code { get; } = code;
}
