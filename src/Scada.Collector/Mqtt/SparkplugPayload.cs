using System.Buffers.Binary;
using System.Text;

namespace Scada.Collector.Mqtt;

/// <summary>Sparkplug B 数据类型（tahu sparkplug_b.proto）。</summary>
public enum SparkplugDataType : uint
{
    Unknown = 0, Int8 = 1, Int16 = 2, Int32 = 3, Int64 = 4, UInt8 = 5, UInt16 = 6, UInt32 = 7, UInt64 = 8,
    Float = 9, Double = 10, Boolean = 11, String = 12, DateTime = 13, Text = 14
}

public sealed class SparkplugMetric
{
    public string? Name { get; set; }
    public ulong? Alias { get; set; }
    public ulong? Timestamp { get; set; }
    public SparkplugDataType DataType { get; set; }
    public bool IsNull { get; set; }
    public bool IsHistorical { get; set; }
    /// <summary>数值型统一以 double 表达；字符串保存在 StringValue。</summary>
    public double? Value { get; set; }
    public string? StringValue { get; set; }
    internal ulong? RawInt, RawLong; internal double? RawFloat, RawDouble; internal bool? RawBool;

    /// <summary>DATA 消息可省略数据类型，此时须按出生证书中声明的类型解释原始字段。</summary>
    public double? ValueAs(SparkplugDataType type) => type switch
    {
        _ when IsNull => null,
        _ when type == DataType || RawInt is null && RawLong is null && RawFloat is null && RawDouble is null && RawBool is null => Value,
        _ => SparkplugPayload.Interpret(type, RawInt, RawLong, RawFloat, RawDouble, RawBool, StringValue)
    };
}

public sealed class SparkplugPayload
{
    public ulong? Timestamp { get; set; }
    public ulong? Seq { get; set; }
    public List<SparkplugMetric> Metrics { get; } = [];

    // ---- 编码：最小 protobuf 实现，仅覆盖 Payload/Metric 的标量字段 ----
    public byte[] Encode()
    {
        var w = new ProtoWriter();
        if (Timestamp is ulong ts) w.Varint(1, ts);
        foreach (var m in Metrics) w.Bytes(2, EncodeMetric(m));
        if (Seq is ulong seq) w.Varint(3, seq);
        return w.ToArray();
    }

    private static byte[] EncodeMetric(SparkplugMetric m)
    {
        var w = new ProtoWriter();
        if (m.Name is not null) w.Bytes(1, Encoding.UTF8.GetBytes(m.Name));
        if (m.Alias is ulong alias) w.Varint(2, alias);
        if (m.Timestamp is ulong ts) w.Varint(3, ts);
        w.Varint(4, (uint)m.DataType);
        if (m.IsHistorical) w.Varint(5, 1);
        if (m.IsNull) { w.Varint(7, 1); return w.ToArray(); }
        var v = m.Value ?? 0;
        switch (m.DataType)
        {
            case SparkplugDataType.Int8 or SparkplugDataType.Int16 or SparkplugDataType.Int32: w.Varint(10, unchecked((uint)(int)v)); break;
            case SparkplugDataType.UInt8 or SparkplugDataType.UInt16 or SparkplugDataType.UInt32: w.Varint(10, (uint)v); break;
            case SparkplugDataType.Int64 or SparkplugDataType.DateTime: w.Varint(11, unchecked((ulong)(long)v)); break;
            case SparkplugDataType.UInt64: w.Varint(11, (ulong)v); break;
            case SparkplugDataType.Float: w.Fixed32(12, BitConverter.SingleToUInt32Bits((float)v)); break;
            case SparkplugDataType.Double: w.Fixed64(13, BitConverter.DoubleToUInt64Bits(v)); break;
            case SparkplugDataType.Boolean: w.Varint(14, v != 0 ? 1UL : 0UL); break;
            case SparkplugDataType.String or SparkplugDataType.Text: w.Bytes(15, Encoding.UTF8.GetBytes(m.StringValue ?? "")); break;
        }
        return w.ToArray();
    }

    public static SparkplugPayload Decode(ReadOnlySpan<byte> data)
    {
        var payload = new SparkplugPayload();
        var r = new ProtoReader(data);
        while (r.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 0: payload.Timestamp = r.Varint(); break;
                case 2 when wire == 2: payload.Metrics.Add(DecodeMetric(r.Bytes())); break;
                case 3 when wire == 0: payload.Seq = r.Varint(); break;
                default: r.Skip(wire); break;
            }
        }
        return payload;
    }

    private static SparkplugMetric DecodeMetric(ReadOnlySpan<byte> data)
    {
        var m = new SparkplugMetric();
        var r = new ProtoReader(data);
        ulong? intValue = null, longValue = null; double? f = null, d = null; bool? b = null;
        while (r.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: m.Name = Encoding.UTF8.GetString(r.Bytes()); break;
                case 2 when wire == 0: m.Alias = r.Varint(); break;
                case 3 when wire == 0: m.Timestamp = r.Varint(); break;
                case 4 when wire == 0: m.DataType = (SparkplugDataType)r.Varint(); break;
                case 5 when wire == 0: m.IsHistorical = r.Varint() != 0; break;
                case 7 when wire == 0: m.IsNull = r.Varint() != 0; break;
                case 10 when wire == 0: intValue = r.Varint(); break;
                case 11 when wire == 0: longValue = r.Varint(); break;
                case 12 when wire == 5: f = BitConverter.UInt32BitsToSingle(r.Fixed32()); break;
                case 13 when wire == 1: d = BitConverter.UInt64BitsToDouble(r.Fixed64()); break;
                case 14 when wire == 0: b = r.Varint() != 0; break;
                case 15 when wire == 2: m.StringValue = Encoding.UTF8.GetString(r.Bytes()); break;
                default: r.Skip(wire); break;
            }
        }
        m.RawInt = intValue; m.RawLong = longValue; m.RawFloat = f; m.RawDouble = d; m.RawBool = b;
        m.Value = Interpret(m.DataType, intValue, longValue, f, d, b, m.StringValue);
        return m;
    }

    internal static double? Interpret(SparkplugDataType type, ulong? intValue, ulong? longValue, double? f, double? d, bool? b, string? text)
    {
        return type switch
        {
            SparkplugDataType.Int8 => intValue is ulong i8 ? (sbyte)(uint)i8 : null,
            SparkplugDataType.Int16 => intValue is ulong i16 ? (short)(uint)i16 : null,
            SparkplugDataType.Int32 => intValue is ulong i32 ? (int)(uint)i32 : null,
            SparkplugDataType.UInt8 or SparkplugDataType.UInt16 or SparkplugDataType.UInt32 => intValue is ulong u ? (uint)u : null,
            SparkplugDataType.Int64 or SparkplugDataType.DateTime => longValue is ulong l ? (long)l : null,
            SparkplugDataType.UInt64 => longValue is ulong ul ? ul : null,
            SparkplugDataType.Float => f,
            SparkplugDataType.Double => d,
            SparkplugDataType.Boolean => b is bool bv ? (bv ? 1 : 0) : null,
            SparkplugDataType.String or SparkplugDataType.Text => double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sv) ? sv : null,
            _ => intValue ?? longValue ?? f ?? d ?? (b is bool bb ? (bb ? 1 : 0) : null)
        };
    }

    private sealed class ProtoWriter
    {
        private readonly MemoryStream _s = new();
        private void Raw(ulong v) { while (v >= 0x80) { _s.WriteByte((byte)(v | 0x80)); v >>= 7; } _s.WriteByte((byte)v); }
        private void Key(int field, int wire) => Raw((ulong)((field << 3) | wire));
        public void Varint(int field, ulong v) { Key(field, 0); Raw(v); }
        public void Bytes(int field, byte[] v) { Key(field, 2); Raw((ulong)v.Length); _s.Write(v); }
        public void Fixed32(int field, uint v) { Key(field, 5); Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _s.Write(b); }
        public void Fixed64(int field, ulong v) { Key(field, 1); Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _s.Write(b); }
        public byte[] ToArray() => _s.ToArray();
    }

    private ref struct ProtoReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        private int _p;
        public bool Next(out int field, out int wire)
        {
            field = 0; wire = 0;
            if (_p >= _d.Length) return false;
            var key = Varint(); field = (int)(key >> 3); wire = (int)(key & 7);
            if (field == 0) throw new FormatException("Sparkplug 载荷字段号无效");
            return true;
        }
        public ulong Varint()
        {
            ulong v = 0; var shift = 0;
            while (true)
            {
                if (_p >= _d.Length || shift > 63) throw new FormatException("Sparkplug 载荷 varint 截断");
                var b = _d[_p++]; v |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) return v;
                shift += 7;
            }
        }
        public ReadOnlySpan<byte> Bytes()
        {
            var len = Varint();
            if (len > (ulong)(_d.Length - _p)) throw new FormatException("Sparkplug 载荷长度越界");
            var s = _d.Slice(_p, (int)len); _p += (int)len; return s;
        }
        public uint Fixed32() { if (_p + 4 > _d.Length) throw new FormatException("截断"); var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[_p..]); _p += 4; return v; }
        public ulong Fixed64() { if (_p + 8 > _d.Length) throw new FormatException("截断"); var v = BinaryPrimitives.ReadUInt64LittleEndian(_d[_p..]); _p += 8; return v; }
        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Fixed64(); break;
                case 2: Bytes(); break;
                case 5: Fixed32(); break;
                default: throw new FormatException($"不支持的 protobuf wire type {wire}");
            }
        }
    }
}
