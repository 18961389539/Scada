using System.Text;

namespace Scada.Protocols.S7;

/// <summary>
/// S7 STRING：首字节为最大长度、次字节为实际长度、其后为 UTF-8 字节。
/// 与寄存器块字符串（<see cref="RegisterValueCodec.DecodeString"/>）不同——这里多 2 字节头，
/// 所以单独一层，别让调用方去记"哪一种字符串"。
/// </summary>
public static class S7StringCodec
{
    public const int HeaderLength = 2;
    public const int MaxLength = 254;

    /// <summary>
    /// 解码。实际长度以三者的最小值截断：声明长度、测点配置长度、块内可用字节——
    /// PLC 里改过字符串长度而组态没同步时，读出来的是可用部分而不是越界异常。
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> block, int maxLength = int.MaxValue)
    {
        if (block.Length <= HeaderLength) return "";
        var available = Math.Min(block[0], Math.Min(maxLength, block.Length - HeaderLength));
        var count = Math.Min(block[1], available);
        return count <= 0 ? "" : Encoding.UTF8.GetString(block.Slice(HeaderLength, count));
    }

    /// <summary>编码。超出 maxLength 的部分按完整字符截断，首字节写最大长度、次字节写实际字节数。</summary>
    public static byte[] Encode(string value, int maxLength)
    {
        if (maxLength is < 0 or > MaxLength)
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, $"S7 STRING 长度须为 0～{MaxLength} 字节");

        var bytes = new byte[maxLength + HeaderLength];
        var count = 0;
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            var encoded = rune.EncodeToUtf8(buffer);
            if (count + encoded > maxLength) break;
            buffer[..encoded].CopyTo(bytes.AsSpan(HeaderLength + count));
            count += encoded;
        }

        bytes[0] = (byte)maxLength;
        bytes[1] = (byte)count;
        return bytes;
    }
}
