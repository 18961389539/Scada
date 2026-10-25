using Scada.Core.Enums;

namespace Scada.Collector.Common;

/// <summary>16 位字数组与数值的互转（多字值按 lowWordFirst 指定字顺序，字内按协议给出的 ushort）。</summary>
public static class WordCodec
{
    public static double Decode(ReadOnlySpan<ushort> words, TagDataType type, bool lowWordFirst)
    {
        var count = WordCount(type);
        if (words.Length < count) throw new IOException("数据长度不足");
        ulong combined = 0;
        for (var i = 0; i < count; i++) combined |= (ulong)words[lowWordFirst ? i : count - 1 - i] << (16 * i);
        return type switch
        {
            TagDataType.Bool => words[0] != 0 ? 1 : 0,
            TagDataType.Int16 => (short)words[0],
            TagDataType.UInt16 => words[0],
            TagDataType.Int32 => (int)(uint)combined,
            TagDataType.UInt32 => (uint)combined,
            TagDataType.Float => BitConverter.UInt32BitsToSingle((uint)combined),
            TagDataType.Double => BitConverter.UInt64BitsToDouble(combined),
            _ => throw new InvalidOperationException("不支持的数据类型")
        };
    }

    public static ushort[] Encode(double value, TagDataType type, bool lowWordFirst)
    {
        ulong raw; int count;
        switch (type)
        {
            case TagDataType.Bool: raw = value != 0 ? 1UL : 0; count = 1; break;
            case TagDataType.Int16: raw = (ushort)checked((short)value); count = 1; break;
            case TagDataType.UInt16: raw = checked((ushort)value); count = 1; break;
            case TagDataType.Int32: raw = (uint)checked((int)value); count = 2; break;
            case TagDataType.UInt32: raw = checked((uint)value); count = 2; break;
            case TagDataType.Float: raw = BitConverter.SingleToUInt32Bits((float)value); count = 2; break;
            case TagDataType.Double: raw = BitConverter.DoubleToUInt64Bits(value); count = 4; break;
            default: throw new InvalidOperationException("不支持的数据类型");
        }
        var words = new ushort[count];
        for (var i = 0; i < count; i++) words[lowWordFirst ? i : count - 1 - i] = (ushort)(raw >> (16 * i));
        return words;
    }

    public static int WordCount(TagDataType type) => type switch
    {
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Double => 4,
        _ => 1
    };
}

/// <summary>按地址合并连续读取区间。</summary>
public static class SpanMerger
{
    public sealed record Span<T>(int Start, int Length, IReadOnlyList<(T Item, int Offset, int Length)> Items);

    public static List<Span<T>> Merge<T>(IEnumerable<(T Item, int Start, int Length)> items, int maxGap, int maxLength)
    {
        var result = new List<Span<T>>();
        var pending = new List<(T Item, int Start, int Length)>();
        int start = 0, end = 0;
        void Flush()
        {
            if (pending.Count == 0) return;
            result.Add(new(start, end - start, pending.Select(x => (x.Item, x.Start - start, x.Length)).ToArray()));
            pending.Clear();
        }
        foreach (var item in items.OrderBy(x => x.Start))
        {
            var itemEnd = item.Start + item.Length;
            if (pending.Count > 0 && (item.Start - end > maxGap || Math.Max(end, itemEnd) - start > maxLength)) Flush();
            if (pending.Count == 0) { start = item.Start; end = itemEnd; }
            else end = Math.Max(end, itemEnd);
            pending.Add(item);
        }
        Flush();
        return result;
    }
}
