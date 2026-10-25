using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Scada.Core.Protocols;

namespace Scada.Collector.Mitsubishi;

/// <summary>MC 协议 3E 帧（二进制/ASCII）批量读写编解码。</summary>
public static class McFrame
{
    public const ushort CmdBatchRead = 0x0401, CmdBatchWrite = 0x1401;
    public const ushort SubWord = 0x0000, SubBit = 0x0001;

    public static byte[] Request(McDeviceOptions o, ushort command, ushort sub, McDeviceKind device, int head, int points, ReadOnlySpan<byte> binaryData = default, string asciiData = "")
    {
        if (o.Format == McFormat.Binary)
        {
            var body = new byte[2 + 2 + 2 + 3 + 1 + 2 + binaryData.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)o.MonitoringTimer);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), command);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), sub);
            body[6] = (byte)head; body[7] = (byte)(head >> 8); body[8] = (byte)(head >> 16);
            body[9] = device.BinaryCode;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), (ushort)points);
            binaryData.CopyTo(body.AsSpan(12));
            var frame = new byte[9 + body.Length];
            frame[0] = 0x50; frame[1] = 0x00; frame[2] = (byte)o.NetworkNo; frame[3] = (byte)o.PcNo;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)o.ModuleIo); frame[6] = (byte)o.ModuleStation;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(7), (ushort)body.Length);
            body.CopyTo(frame.AsSpan(9));
            return frame;
        }
        var number = device.HexNumber ? head.ToString("X6", CultureInfo.InvariantCulture) : head.ToString("D6", CultureInfo.InvariantCulture);
        var text = $"{o.MonitoringTimer:X4}{command:X4}{sub:X4}{device.AsciiCode}{number}{points:X4}{asciiData}";
        return Encoding.ASCII.GetBytes($"5000{o.NetworkNo:X2}{o.PcNo:X2}{o.ModuleIo:X4}{o.ModuleStation:X2}{text.Length:X4}{text}");
    }

    /// <summary>TCP 流中判断完整应答长度。</summary>
    public static int? ResponseLength(McFormat format, ReadOnlySpan<byte> data)
    {
        if (format == McFormat.Binary) return data.Length < 9 ? null : 9 + BinaryPrimitives.ReadUInt16LittleEndian(data[7..]);
        if (data.Length < 18) return null;
        return int.TryParse(Encoding.ASCII.GetString(data.Slice(14, 4)), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? 18 + n : throw new IOException("MC ASCII 应答长度字段无效");
    }

    /// <summary>校验应答头与结束代码，返回应答数据区（二进制为原始字节，ASCII 为十六进制文本字节）。</summary>
    public static ReadOnlySpan<byte> ResponseData(McDeviceOptions o, ReadOnlySpan<byte> frame)
    {
        if (o.Format == McFormat.Binary)
        {
            if (frame.Length < 11 || frame[0] != 0xD0 || frame[1] != 0x00) throw new IOException("MC 应答副头部无效");
            if (frame[2] != (byte)o.NetworkNo || frame[3] != (byte)o.PcNo) throw new IOException("MC 应答网络号/PC 号不匹配");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(frame[7..]);
            if (frame.Length != 9 + length) throw new IOException("MC 应答长度不一致");
            var end = BinaryPrimitives.ReadUInt16LittleEndian(frame[9..]);
            if (end != 0) throw new McEndCodeException(end, $"PLC 返回结束代码 0x{end:X4}（{Describe(end)}）");
            return frame[11..];
        }
        var text = Encoding.ASCII.GetString(frame);
        if (text.Length < 22 || !text.StartsWith("D000", StringComparison.Ordinal)) throw new IOException("MC ASCII 应答副头部无效");
        if (text.Substring(4, 2) != $"{o.NetworkNo:X2}" || !text.Substring(6, 2).Equals($"{o.PcNo:X2}", StringComparison.OrdinalIgnoreCase)) throw new IOException("MC 应答网络号/PC 号不匹配");
        var code = ushort.Parse(text.AsSpan(18, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (code != 0) throw new McEndCodeException(code, $"PLC 返回结束代码 0x{code:X4}（{Describe(code)}）");
        return frame[22..];
    }

    public static ushort[] Words(McFormat format, ReadOnlySpan<byte> data, int count)
    {
        var words = new ushort[count];
        if (format == McFormat.Binary)
        {
            if (data.Length != count * 2) throw new IOException("MC 应答字数不符");
            for (var i = 0; i < count; i++) words[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
            return words;
        }
        if (data.Length != count * 4) throw new IOException("MC 应答字数不符");
        for (var i = 0; i < count; i++) words[i] = ushort.Parse(Encoding.ASCII.GetString(data.Slice(i * 4, 4)), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return words;
    }

    public static bool[] Bits(McFormat format, ReadOnlySpan<byte> data, int count)
    {
        var bits = new bool[count];
        if (format == McFormat.Binary)
        {
            // 位单位：每点 4 位，高半字节在前。
            if (data.Length != (count + 1) / 2) throw new IOException("MC 应答位数不符");
            for (var i = 0; i < count; i++) bits[i] = ((i % 2 == 0 ? data[i / 2] >> 4 : data[i / 2]) & 0x1) != 0;
            return bits;
        }
        if (data.Length != count) throw new IOException("MC 应答位数不符");
        for (var i = 0; i < count; i++) bits[i] = data[i] switch { (byte)'1' => true, (byte)'0' => false, _ => throw new IOException("MC ASCII 位数据无效") };
        return bits;
    }

    public static (byte[] Binary, string Ascii) WordData(ushort[] words)
    {
        var bin = new byte[words.Length * 2];
        for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bin.AsSpan(i * 2), words[i]);
        return (bin, string.Concat(words.Select(w => w.ToString("X4", CultureInfo.InvariantCulture))));
    }

    public static (byte[] Binary, string Ascii) BitData(bool value) => (new[] { (byte)(value ? 0x10 : 0x00) }, value ? "1" : "0");

    private static string Describe(ushort code) => code switch
    {
        0xC050 => "ASCII 数据无法转换",
        0xC051 or 0xC052 or 0xC053 or 0xC054 => "读写点数超出允许范围",
        0xC056 => "地址超出范围",
        0xC059 => "命令/子命令不支持",
        0xC05B => "无法读写该软元件",
        0xC05C => "请求内容错误",
        0xC061 => "请求数据长度不符",
        0x4031 => "软元件不存在或超出范围",
        _ => "参见 PLC 手册错误代码"
    };
}

public sealed class McEndCodeException(ushort code, string message) : IOException(message)
{
    public ushort Code { get; } = code;
}
