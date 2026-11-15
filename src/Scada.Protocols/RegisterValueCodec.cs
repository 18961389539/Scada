using System.Buffers.Binary;
using System.Text;
using Scada.Core.Enums;

namespace Scada.Protocols;

/// <summary>
/// 寄存器值层编解码：把"数据类型 + 字节序"翻译成寄存器块语义，与具体协议无关。
/// Modbus 保持寄存器、S7 的 DB 字节区、Modbus 从站上送值都走这一套，所以不叫 Modbus*。
///
/// 字节序定义（32 位与 64 位一致，四种排列都作用在整段字节上）：
///   ABCD 原序、BADC 每个 16 位字内换字节、CDAB 交换 32 位字、DCBA 整体反序。
/// 16 位只有"换字节/不换"两种（BADC 换，其余按原序）；Bool 固定取寄存器低字节 bit0，与字节序选项无关。
///
/// 四张排列表都是自逆的（排列两次回到原序），编码与解码因此共用同一张表。
/// 代价是"编码→解码"永远自洽，表标错了也测不出来，所以每个字节序都必须有 wire 字节的黄金用例。
/// </summary>
public static class RegisterValueCodec
{
    private const string Layer = "值层";

    /// <summary>该类型占几个 16 位寄存器。</summary>
    public static int RegisterCount(TagDataType dataType) => dataType switch
    {
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Double => 4,
        _ => 1
    };

    /// <summary>该类型占几个字节。字符串按 <see cref="RegisterCount(TagDataType)"/> 的兜底值 1 寄存器算。</summary>
    public static int ByteCount(TagDataType dataType) => RegisterCount(dataType) * 2;

    public static bool IsRegisterType(TagDataType dataType) => dataType
        is TagDataType.Bool or TagDataType.Int16 or TagDataType.UInt16
        or TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float or TagDataType.Double;

    /// <summary>按测点类型与字节序解析一个值。块长度不足或类型不支持时抛 <see cref="ProtocolFormatException"/>。</summary>
    public static double DecodeRegister(ReadOnlySpan<byte> block, int registerOffset, TagDataType dataType, ByteOrder byteOrder)
    {
        if (!TryDecodeRegister(block, registerOffset, dataType, byteOrder, out var value, out var error))
            throw new ProtocolFormatException(Layer, error!);
        return value;
    }

    /// <summary>
    /// 解析失败的原因（块不够长、类型不是寄存器值）而不是"设备拒绝了"，
    /// 采集侧据此把测点置 Bad 并带上可读的错误文案，不必依赖异常。
    /// </summary>
    public static bool TryDecodeRegister(ReadOnlySpan<byte> block, int registerOffset, TagDataType dataType,
        ByteOrder byteOrder, out double value, out string? error)
    {
        value = 0;
        if (registerOffset < 0)
        {
            error = $"寄存器偏移不能为负：{registerOffset}";
            return false;
        }

        if (!IsRegisterType(dataType))
        {
            error = $"数据类型 {dataType} 不是寄存器值（文本请用 DecodeString）";
            return false;
        }

        var size = ByteCount(dataType);
        // 用 long 算偏移：寄存器号来自组态/地址运算，*2 溢出成负数会绕过下面的长度检查
        var offset = (long)registerOffset * 2;
        if (offset + size > block.Length)
        {
            error = $"寄存器块 {block.Length} 字节，不足以在字节 {offset}（寄存器 {registerOffset}）读取 {dataType}，需要 {size} 字节";
            return false;
        }

        error = null;
        var word = block.Slice((int)offset, size);
        // Bool 是"寄存器里的第 0 位"，不参与字节序重排。
        if (dataType == TagDataType.Bool)
        {
            value = word[1] & 0x01;
            return true;
        }

        Span<byte> canonical = stackalloc byte[8];
        Reorder(word, byteOrder, canonical);
        value = dataType switch
        {
            TagDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(canonical),
            TagDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(canonical),
            TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(canonical),
            TagDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(canonical),
            TagDataType.Float => BinaryPrimitives.ReadSingleBigEndian(canonical),
            TagDataType.Double => BinaryPrimitives.ReadDoubleBigEndian(canonical),
            _ => 0
        };
        return true;
    }

    /// <summary>线圈/离散输入位：LSB 优先，bit i 位于 byte i/8 的第 i%8 位。</summary>
    public static bool DecodeCoil(ReadOnlySpan<byte> block, int bitOffset)
    {
        if (bitOffset < 0) throw new ProtocolFormatException(Layer, $"位偏移不能为负：{bitOffset}");
        var index = bitOffset / 8;
        if (index >= block.Length)
            throw new ProtocolFormatException(Layer, $"位块 {block.Length} 字节，不足以读取第 {bitOffset} 位（需第 {index} 字节）");
        return (block[index] & (1 << (bitOffset % 8))) != 0;
    }

    // 多寄存器字符串按字节流解码：寄存器高字节在前，UTF-8，去掉尾部填充 0。
    // 长度不足时按可用字节截断——字符串本来就是变长的，读短了不该让整块采集失败。
    public static string DecodeString(ReadOnlySpan<byte> block, int registerOffset, int length)
    {
        if (registerOffset < 0 || length <= 0) return "";
        var offset = (long)registerOffset * 2;
        if (offset >= block.Length) return "";
        var available = (int)Math.Min(length, block.Length - offset);
        return Encoding.UTF8.GetString(block.Slice((int)offset, available)).TrimEnd('\0');
    }

    /// <summary>编码为寄存器字节块（UTF-8，偶数长度）。超出长度的部分按完整字符截断，不拆半个字符。</summary>
    public static byte[] EncodeString(string value, int length)
    {
        if (length <= 0) return [];
        var payload = new List<byte>(length);
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            var count = rune.EncodeToUtf8(buffer);
            if (payload.Count + count > length) break;
            for (var i = 0; i < count; i++) payload.Add(buffer[i]);
        }

        var bytes = new byte[(length + 1) / 2 * 2];
        payload.CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// 把值编码成 wire 字节。数值超出测点类型范围、或不是有限值时抛 <see cref="ArgumentOutOfRangeException"/>：
    /// 写路径上"写进去的是另一个值"比"写失败"危险得多，所以这里必须失败而不是饱和截断。
    /// </summary>
    public static byte[] EncodeRegister(double value, TagDataType dataType, ByteOrder byteOrder)
    {
        if (!IsRegisterType(dataType))
            throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "不是寄存器值类型");

        var bytes = new byte[ByteCount(dataType)];
        if (!TryEncodeRegister(value, dataType, byteOrder, bytes, out var error))
            throw new ArgumentOutOfRangeException(nameof(value), value, error);
        return bytes;
    }

    /// <summary>编码到调用方缓冲区。失败只写 error，不动 destination 的剩余部分。</summary>
    public static bool TryEncodeRegister(double value, TagDataType dataType, ByteOrder byteOrder,
        Span<byte> destination, out string? error)
    {
        error = null;
        var size = ByteCount(dataType);
        if (destination.Length < size)
        {
            error = $"目标缓冲区 {destination.Length} 字节，不足以容纳 {dataType} 的 {size} 字节";
            return false;
        }

        if (dataType == TagDataType.Bool)
        {
            destination[0] = 0;
            destination[1] = value != 0 ? (byte)1 : (byte)0;
            return true;
        }

        Span<byte> canonical = stackalloc byte[8];
        switch (dataType)
        {
            case TagDataType.Int16:
                if (!TryRound(value, short.MinValue, short.MaxValue, "Int16", out var i16, out error)) return false;
                BinaryPrimitives.WriteInt16BigEndian(canonical, (short)i16);
                break;
            case TagDataType.UInt16:
                if (!TryRound(value, ushort.MinValue, ushort.MaxValue, "UInt16", out var u16, out error)) return false;
                BinaryPrimitives.WriteUInt16BigEndian(canonical, (ushort)u16);
                break;
            case TagDataType.Int32:
                if (!TryRound(value, int.MinValue, int.MaxValue, "Int32", out var i32, out error)) return false;
                BinaryPrimitives.WriteInt32BigEndian(canonical, (int)i32);
                break;
            case TagDataType.UInt32:
                if (!TryRound(value, uint.MinValue, uint.MaxValue, "UInt32", out var u32, out error)) return false;
                BinaryPrimitives.WriteUInt32BigEndian(canonical, (uint)u32);
                break;
            case TagDataType.Float:
                if (!double.IsFinite(value) || !float.IsFinite((float)value))
                {
                    error = $"Float 测点不接受非有限值或超出单精度范围的值：{value}";
                    return false;
                }

                BinaryPrimitives.WriteSingleBigEndian(canonical, (float)value);
                break;
            case TagDataType.Double:
                if (!double.IsFinite(value))
                {
                    error = $"Double 测点不接受非有限值：{value}";
                    return false;
                }

                BinaryPrimitives.WriteDoubleBigEndian(canonical, value);
                break;
            default:
                error = $"数据类型 {dataType} 不是寄存器值";
                return false;
        }

        Reorder(canonical[..size], byteOrder, destination);
        return true;
    }

    // 取整用 AwayFromZero：现场设定值 2.5 送成 3 才符合直觉，银行家舍入的 2.5→2 会被当成丢精度。
    private static bool TryRound(double value, double min, double max, string type, out double rounded, out string? error)
    {
        rounded = 0;
        error = null;
        if (!double.IsFinite(value))
        {
            error = $"{type} 测点不接受非有限值：{value}";
            return false;
        }

        var result = Math.Round(value, MidpointRounding.AwayFromZero);
        if (result < min || result > max)
        {
            error = $"{type} 测点取值范围 {min}～{max}，实际 {value}";
            return false;
        }

        rounded = result;
        return true;
    }

    /// <summary>把 wire 字节重排成大端规范序（解码），或把大端规范序重排成 wire 字节（编码）——排列表自逆，两者共用。</summary>
    private static void Reorder(ReadOnlySpan<byte> source, ByteOrder byteOrder, Span<byte> destination)
    {
        if (source.Length == 2)
        {
            if (byteOrder == ByteOrder.BADC)
            {
                destination[0] = source[1];
                destination[1] = source[0];
            }
            else
            {
                destination[0] = source[0];
                destination[1] = source[1];
            }

            return;
        }

        var permutation = source.Length == 4 ? Permutation32(byteOrder) : Permutation64(byteOrder);
        for (var i = 0; i < source.Length; i++) destination[i] = source[permutation[i]];
    }

    private static ReadOnlySpan<byte> Permutation32(ByteOrder byteOrder) => byteOrder switch
    {
        ByteOrder.BADC => [1, 0, 3, 2],
        ByteOrder.CDAB => [2, 3, 0, 1],
        ByteOrder.DCBA => [3, 2, 1, 0],
        _ => [0, 1, 2, 3]
    };

    private static ReadOnlySpan<byte> Permutation64(ByteOrder byteOrder) => byteOrder switch
    {
        ByteOrder.BADC => [1, 0, 3, 2, 5, 4, 7, 6],
        ByteOrder.CDAB => [2, 3, 0, 1, 6, 7, 4, 5],
        ByteOrder.DCBA => [7, 6, 5, 4, 3, 2, 1, 0],
        _ => [0, 1, 2, 3, 4, 5, 6, 7]
    };
}
