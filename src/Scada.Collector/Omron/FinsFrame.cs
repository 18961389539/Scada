using System.Buffers.Binary;

namespace Scada.Collector.Omron;

public sealed class FinsEndCodeException(ushort code, string message) : IOException(message) { public ushort Code { get; } = code; }

/// <summary>FINS 命令帧与 FINS/TCP 封装。</summary>
public static class FinsFrame
{
    public const ushort MemoryAreaRead = 0x0101, MemoryAreaWrite = 0x0102;

    public sealed record Route(byte DestNetwork, byte DestNode, byte DestUnit, byte SourceNetwork, byte SourceNode, byte SourceUnit = 0);

    public static byte[] Command(Route r, byte sid, ushort command, byte areaCode, int word, int bit, int count, ReadOnlySpan<byte> data = default)
    {
        var frame = new byte[10 + 2 + 6 + data.Length];
        frame[0] = 0x80; frame[1] = 0x00; frame[2] = 0x02;
        frame[3] = r.DestNetwork; frame[4] = r.DestNode; frame[5] = r.DestUnit;
        frame[6] = r.SourceNetwork; frame[7] = r.SourceNode; frame[8] = r.SourceUnit; frame[9] = sid;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), command);
        frame[12] = areaCode; BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13), (ushort)word); frame[15] = (byte)bit;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)count);
        data.CopyTo(frame.AsSpan(18));
        return frame;
    }

    /// <summary>校验应答路由、SID、命令码与结束代码，返回数据区。</summary>
    public static ReadOnlySpan<byte> Response(Route r, byte sid, ushort command, ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14) throw new IOException("FINS 应答过短");
        if ((frame[0] & 0x40) == 0) throw new IOException("FINS 帧不是应答");
        if (frame[9] != sid) throw new IOException($"FINS 应答 SID 不匹配（期望 {sid}，收到 {frame[9]}）");
        if (frame[3] != r.SourceNetwork || frame[4] != r.SourceNode || frame[6] != r.DestNetwork || frame[7] != r.DestNode) throw new IOException("FINS 应答路由不匹配");
        if (BinaryPrimitives.ReadUInt16BigEndian(frame[10..]) != command) throw new IOException("FINS 应答命令码不匹配");
        // 结束代码：MRES bit7 为中继错误标志，SRES bit6/7 为 CPU 致命/非致命错误标志，不影响本次读写结果。
        var code = (ushort)(((frame[12] & 0x7F) << 8) | (frame[13] & 0x3F));
        if (code != 0) throw new FinsEndCodeException(code, $"PLC 返回结束代码 0x{code:X4}（{Describe(code)}）");
        return frame[14..];
    }

    // ---- FINS/TCP ----
    public static byte[] TcpHandshake(byte clientNode)
    {
        var b = new byte[20];
        "FINS"u8.CopyTo(b); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 12); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), clientNode); return b;
    }

    public static byte[] TcpWrap(byte[] fins)
    {
        var b = new byte[16 + fins.Length];
        "FINS"u8.CopyTo(b); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)(8 + fins.Length)); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 2);
        fins.CopyTo(b, 16); return b;
    }

    public static int? TcpLength(ReadOnlySpan<byte> data) => data.Length < 8 ? null : 8 + (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);

    /// <summary>解析 FINS/TCP 帧，返回 (命令, 错误码, 负载)。</summary>
    public static (uint Command, uint Error, byte[] Payload) TcpUnwrap(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 16 || !frame[..4].SequenceEqual("FINS"u8)) throw new IOException("FINS/TCP 帧头无效");
        var command = BinaryPrimitives.ReadUInt32BigEndian(frame[8..]); var error = BinaryPrimitives.ReadUInt32BigEndian(frame[12..]);
        if (error != 0) throw new IOException($"FINS/TCP 错误码 0x{error:X8}（{TcpError(error)}）");
        return (command, error, frame[16..].ToArray());
    }

    private static string TcpError(uint e) => e switch
    {
        1 => "帧头不是 FINS", 2 => "数据过长", 3 => "命令不支持", 0x20 => "所有连接已占用", 0x21 => "指定节点已连接",
        0x22 => "尝试访问受保护节点", 0x23 => "客户端节点号超出范围", 0x24 => "节点号重复", 0x25 => "所有节点号已占用", _ => "参见 PLC 手册"
    };

    private static string Describe(ushort code) => code switch
    {
        0x0101 => "本地节点未加入网络", 0x0102 => "令牌超时", 0x0201 => "目标节点未加入网络", 0x0204 => "目标节点忙",
        0x0205 => "响应超时", 0x0401 => "命令未定义", 0x1001 => "命令过长", 0x1002 => "命令过短", 0x1003 => "元素数与数据不符",
        0x1101 => "区域类型不存在", 0x1103 => "地址超出范围", 0x1104 => "地址范围越界", 0x2101 => "区域只读", 0x2102 => "区域受保护",
        0x2205 => "PLC 处于不允许写入的模式", _ => "参见 FINS 结束代码表"
    };
}
