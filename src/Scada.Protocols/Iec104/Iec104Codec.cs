using System.Buffers.Binary;

namespace Scada.Protocols.Iec104;

public enum ApciKind { I, S, U }

/// <summary>帧扫描结果：<see cref="Ready"/> 表示缓冲区头部已是一个完整 APDU。</summary>
public enum FrameScan { NeedMore, Ready }

/// <summary>IEC 60870-5-104 类型标识、传送原因与 U 帧功能码（数值即标准编码）。</summary>
public static class Iec104Types
{
    // 监视方向，不带时标
    public const byte M_SP_NA = 1;
    public const byte M_DP_NA = 3;
    public const byte M_ST_NA = 5;
    public const byte M_BO_NA = 7;
    public const byte M_ME_NA = 9;
    public const byte M_ME_NB = 11;
    public const byte M_ME_NC = 13;
    public const byte M_IT_NA = 15;
    public const byte M_ME_ND = 21;

    // 监视方向，带 CP56Time2a 时标
    public const byte M_SP_TB = 30;
    public const byte M_DP_TB = 31;
    public const byte M_ST_TB = 32;
    public const byte M_BO_TB = 33;
    public const byte M_ME_TD = 34;
    public const byte M_ME_TE = 35;
    public const byte M_ME_TF = 36;
    public const byte M_IT_TB = 37;

    // 控制方向
    public const byte C_SC_NA = 45;
    public const byte C_DC_NA = 46;
    public const byte C_RC_NA = 47;
    public const byte C_SE_NA = 48;
    public const byte C_SE_NB = 49;
    public const byte C_SE_NC = 50;

    public const byte M_EI_NA = 70;
    public const byte C_IC_NA = 100;
    public const byte C_CI_NA = 101;
    public const byte C_CS_NA = 103;

    // 传送原因
    public const byte CotSpont = 3;
    public const byte CotReq = 5;
    public const byte CotAct = 6;
    public const byte CotActCon = 7;
    public const byte CotDeact = 8;
    public const byte CotActTerm = 10;
    /// <summary>响应站召唤（原命名 CotInrogen）。</summary>
    public const byte CotInterrogation = 20;
    /// <summary>响应电度量召唤（原命名 CotReqcogen）。</summary>
    public const byte CotCounterInterrogation = 37;

    // U 帧功能码
    public const byte U_StartDtAct = 0x07;
    public const byte U_StartDtCon = 0x0B;
    public const byte U_StopDtAct = 0x13;
    public const byte U_StopDtCon = 0x23;
    public const byte U_TestFrAct = 0x43;
    public const byte U_TestFrCon = 0x83;
}

public sealed record Apdu(ApciKind Kind, int SendSeq, int RecvSeq, byte UFunction, Asdu? Asdu);

/// <summary>
/// ASDU。参数按国内常用 profile 固定：传送原因 1 字节 + 源发地址 1 字节、公共地址 2 字节、信息对象地址 3 字节。
/// 2 字节传送原因或 2 字节信息对象地址的变体没有实现——那属于链路协商，不能靠猜。
/// <paramref name="Unsupported"/> 为 true 表示类型标识不认识、对象没有被解析（而不是"没有对象"）。
/// </summary>
public sealed record Asdu(byte TypeId, bool Sequence, byte Cause, bool Negative, bool Test, byte Originator,
    int CommonAddress, IReadOnlyList<InfoObject> Objects, bool Unsupported = false);

/// <summary>信息对象：地址 + 原始元素字节（含品质描述与时标）。</summary>
public sealed record InfoObject(int Ioa, byte[] Element);

public static class Iec104Codec
{
    public const byte StartByte = 0x68;
    /// <summary>APCI 固定 6 字节：起始字节、长度、四个控制域字节。</summary>
    public const int ApciLength = 6;
    /// <summary>ASDU 上限 249 字节（APDU 总长 255）。</summary>
    public const int MaxAsduLength = 249;
    public const int SequenceModulo = 32768;
    private const int MaxObjectCount = 127;

    public static int ElementLength(byte type) => type switch
    {
        Iec104Types.M_SP_NA or Iec104Types.M_DP_NA => 1,
        Iec104Types.M_ST_NA => 2,
        Iec104Types.M_BO_NA => 5,
        Iec104Types.M_ME_NA or Iec104Types.M_ME_NB => 3,
        Iec104Types.M_ME_NC => 5,
        Iec104Types.M_IT_NA => 5,
        Iec104Types.M_ME_ND => 2,
        Iec104Types.M_SP_TB or Iec104Types.M_DP_TB => 8,
        Iec104Types.M_ST_TB => 9,
        Iec104Types.M_BO_TB => 12,
        Iec104Types.M_ME_TD or Iec104Types.M_ME_TE => 10,
        Iec104Types.M_ME_TF or Iec104Types.M_IT_TB => 12,
        Iec104Types.C_SC_NA or Iec104Types.C_DC_NA or Iec104Types.C_RC_NA => 1,
        Iec104Types.C_SE_NA or Iec104Types.C_SE_NB => 3,
        Iec104Types.C_SE_NC => 5,
        Iec104Types.M_EI_NA => 1,
        Iec104Types.C_IC_NA or Iec104Types.C_CI_NA => 1,
        Iec104Types.C_CS_NA => 7,
        _ => -1
    };

    /// <summary>类型标识是否已实现（未知类型只保留 ASDU 头，便于应答但不解析对象）。</summary>
    public static bool IsSupportedType(byte type) => ElementLength(type) >= 0;

    public static byte[] U(byte function) => [StartByte, 4, function, 0, 0, 0];

    public static byte[] S(int recv)
    {
        CheckSequence(recv, nameof(recv));
        var frame = new byte[ApciLength];
        frame[0] = StartByte;
        frame[1] = 4;
        frame[2] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)(recv << 1));
        return frame;
    }

    public static byte[] I(int send, int recv, Asdu asdu)
    {
        CheckSequence(send, nameof(send));
        CheckSequence(recv, nameof(recv));
        Span<byte> body = stackalloc byte[MaxAsduLength];
        var length = WriteAsdu(asdu, body);
        var frame = new byte[ApciLength + length];
        frame[0] = StartByte;
        frame[1] = (byte)(4 + length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)(send << 1));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)(recv << 1));
        body[..length].CopyTo(frame.AsSpan(ApciLength));
        return frame;
    }

    public static byte[] EncodeAsdu(Asdu asdu)
    {
        Span<byte> buffer = stackalloc byte[MaxAsduLength];
        return buffer[..WriteAsdu(asdu, buffer)].ToArray();
    }

    /// <summary>
    /// 把 ASDU 写进调用方缓冲区并返回字节数。所有会被"静默截断"的字段（VSQ 个数、传送原因、公共地址、
    /// 信息对象地址）在这里校验：越界宁可抛错，也不能发出一个对端会误解的帧。
    /// </summary>
    public static int WriteAsdu(Asdu asdu, Span<byte> destination)
    {
        var objects = asdu.Objects;
        if (objects.Count > MaxObjectCount)
            throw new ProtocolFormatException("IEC104", $"信息对象 {objects.Count} 个，超过 VSQ 低 7 位的上限 {MaxObjectCount}");
        if (asdu.Cause > 0x3F)
            throw new ProtocolFormatException("IEC104", $"传送原因 {asdu.Cause} 超过 6 位上限 63");
        if (asdu.CommonAddress is < 0 or > 0xFFFF)
            throw new ProtocolFormatException("IEC104", $"公共地址 {asdu.CommonAddress} 超出 16 位范围");
        if (destination.Length < ApciLength)
            throw new ProtocolFormatException("IEC104", $"目标缓冲区 {destination.Length} 字节，不足以容纳 ASDU 头");

        destination[0] = asdu.TypeId;
        destination[1] = (byte)((asdu.Sequence ? 0x80 : 0) | objects.Count);
        destination[2] = (byte)(asdu.Cause | (asdu.Negative ? 0x40 : 0) | (asdu.Test ? 0x80 : 0));
        destination[3] = asdu.Originator;
        destination[4] = (byte)asdu.CommonAddress;
        destination[5] = (byte)(asdu.CommonAddress >> 8);

        var expected = ElementLength(asdu.TypeId);
        var position = ApciLength;
        for (var i = 0; i < objects.Count; i++)
        {
            var item = objects[i];
            if (item.Ioa is < 0 or > 0xFFFFFF)
                throw new ProtocolFormatException("IEC104", $"信息对象地址 {item.Ioa} 超出 24 位范围");
            if (!asdu.Sequence || i == 0)
            {
                if (position + 3 > destination.Length)
                    throw new ProtocolFormatException("IEC104", $"ASDU 超过 {MaxAsduLength} 字节上限（写入信息对象地址时溢出）");
                destination[position++] = (byte)item.Ioa;
                destination[position++] = (byte)(item.Ioa >> 8);
                destination[position++] = (byte)(item.Ioa >> 16);
            }

            if (expected >= 0 && item.Element.Length != expected)
                throw new ProtocolFormatException("IEC104",
                    $"类型 {asdu.TypeId} 的信息元素应为 {expected} 字节，实际 {item.Element.Length} 字节（信息对象 {item.Ioa}）");
            if (position + item.Element.Length > destination.Length)
                throw new ProtocolFormatException("IEC104", $"ASDU 超过 {MaxAsduLength} 字节上限（写入信息对象 {item.Ioa} 时溢出）");

            item.Element.CopyTo(destination[position..]);
            position += item.Element.Length;
        }

        return position;
    }

    /// <summary>
    /// 从接收缓冲区头部切一个完整 APDU。起始字节或长度字段非法时按噪声丢弃并继续向后找 0x68，
    /// 而不是直接断链——现场一个杂散字节不该让整条链路重连。
    /// </summary>
    /// <param name="buffer">接收缓冲区，命中后前 <paramref name="frameLength"/> 字节即完整帧。</param>
    /// <param name="count">缓冲区内的有效字节数；丢噪声时会就地压缩并更新。</param>
    /// <param name="frameLength">完整帧长度。</param>
    /// <param name="skipped">本次丢弃的噪声字节数，供上层记日志。</param>
    public static FrameScan Scan(Span<byte> buffer, ref int count, out int frameLength, out int skipped)
    {
        frameLength = 0;
        skipped = 0;
        while (true)
        {
            if (count <= 0) return FrameScan.NeedMore;
            if (buffer[0] != StartByte)
            {
                var index = buffer[..count].IndexOf(StartByte);
                if (index < 0)
                {
                    skipped += count;
                    count = 0;
                    return FrameScan.NeedMore;
                }

                buffer[index..count].CopyTo(buffer);
                skipped += index;
                count -= index;
                continue;
            }

            if (count < 2) return FrameScan.NeedMore;
            var body = buffer[1];
            if (body < 4)
            {
                // 长度字段小于 APCI 的最小值 4：这个 0x68 是噪声，丢掉它继续扫描。
                buffer[1..count].CopyTo(buffer);
                skipped += 1;
                count -= 1;
                continue;
            }

            var total = 2 + body;
            if (count < total) return FrameScan.NeedMore;
            frameLength = total;
            return FrameScan.Ready;
        }
    }

    public static Apdu Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < ApciLength)
            throw new ProtocolFormatException("IEC104", $"APDU 长度 {frame.Length} 不足 {ApciLength} 字节", 0);
        if (frame[0] != StartByte)
            throw new ProtocolFormatException("IEC104", $"起始字节应为 0x{StartByte:X2}，实际 0x{frame[0]:X2}", 0);
        if (frame[1] != frame.Length - 2)
            throw new ProtocolFormatException("IEC104", $"长度字段 {frame[1]} 与实际长度 {frame.Length - 2} 不符", 1);

        var control = frame[2];
        if ((control & 1) == 0)
        {
            var send = BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]) >> 1;
            var recv = BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]) >> 1;
            return new(ApciKind.I, send, recv, 0, DecodeAsdu(frame[ApciLength..]));
        }

        if ((control & 3) == 1)
        {
            if (frame.Length != ApciLength)
                throw new ProtocolFormatException("IEC104", $"S 帧应为 {ApciLength} 字节，实际 {frame.Length} 字节", 1);
            return new(ApciKind.S, 0, BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]) >> 1, 0, null);
        }

        // 两个最低位都是 1 才是 U 帧；其余控制域编码既不是 I/S 也不是 U，直接拒。
        if ((control & 3) == 3) return new(ApciKind.U, 0, 0, control, null);
        throw new ProtocolFormatException("IEC104", $"控制域 0x{control:X2} 不是合法的 I/S/U 帧", 2);
    }

    public static Asdu DecodeAsdu(ReadOnlySpan<byte> data)
    {
        if (data.Length < ApciLength)
            throw new ProtocolFormatException("IEC104", $"ASDU 长度 {data.Length} 不足 6 字节");
        var type = data[0];
        var sequence = (data[1] & 0x80) != 0;
        var count = data[1] & 0x7F;
        var cause = (byte)(data[2] & 0x3F);
        var negative = (data[2] & 0x40) != 0;
        var test = (data[2] & 0x80) != 0;
        var commonAddress = data[4] | data[5] << 8;
        var length = ElementLength(type);
        // 未知类型：只保留头部（调用方据此回否定应答或记一次不支持的告警），不假装"解析出 0 个对象"。
        if (length < 0) return new(type, sequence, cause, negative, test, data[3], commonAddress, [], true);

        var objects = new List<InfoObject>(count);
        var position = ApciLength;
        var ioa = 0;
        for (var i = 0; i < count; i++)
        {
            if (!sequence || i == 0)
            {
                if (position + 3 > data.Length)
                    throw new ProtocolFormatException("IEC104", $"ASDU 在信息对象 {i} 处截断（信息对象地址不完整）", position);
                ioa = data[position] | data[position + 1] << 8 | data[position + 2] << 16;
                position += 3;
            }
            else ioa++;

            if (position + length > data.Length)
                throw new ProtocolFormatException("IEC104", $"ASDU 在信息对象 {ioa} 处截断（需 {length} 字节元素）", position);
            objects.Add(new(ioa, data.Slice(position, length).ToArray()));
            position += length;
        }

        if (position != data.Length)
            throw new ProtocolFormatException("IEC104",
                $"ASDU 长度与信息对象数量不符：解析 {count} 个对象用掉 {position} 字节，实际 {data.Length} 字节", position);
        return new(type, sequence, cause, negative, test, data[3], commonAddress, objects);
    }

    private static void CheckSequence(int value, string name)
    {
        if (value is < 0 or >= SequenceModulo)
            throw new ProtocolFormatException("IEC104", $"发送/接收序号 {name}={value} 超出 0～{SequenceModulo - 1}");
    }

    // ---- CP56Time2a ----

    /// <summary>编码 CP56Time2a（调用方决定传本地时间还是 UTC；IV/SU 位保持 0）。年份只支持 2000–2099。</summary>
    public static byte[] Cp56(DateTime time)
    {
        if (time.Year is < 2000 or > 2099)
            throw new ArgumentOutOfRangeException(nameof(time), time, "CP56Time2a 的年份字段只有 7 位，只能表示 2000–2099 年");
        var milliseconds = time.Second * 1000 + time.Millisecond;
        var dayOfWeek = (int)time.DayOfWeek == 0 ? 7 : (int)time.DayOfWeek; // 1=周一 … 7=周日
        return
        [
            (byte)milliseconds, (byte)(milliseconds >> 8), (byte)time.Minute, (byte)time.Hour,
            (byte)(time.Day | (dayOfWeek << 5)), (byte)time.Month, (byte)(time.Year - 2000)
        ];
    }

    /// <summary>解析 CP56Time2a，无效（IV 位）或字段越界返回 null。</summary>
    public static long? Cp56Ms(ReadOnlySpan<byte> time, bool utc)
    {
        if (time.Length < 7 || (time[2] & 0x80) != 0) return null;
        var milliseconds = time[0] | time[1] << 8;
        var minute = time[2] & 0x3F;
        var hour = time[3] & 0x1F;
        var day = time[4] & 0x1F;
        var month = time[5] & 0x0F;
        var year = 2000 + (time[6] & 0x7F);
        if (milliseconds > 59999 || minute > 59 || hour > 23 || day is < 1 or > 31 || month is < 1 or > 12
            || day > DateTime.DaysInMonth(year, month)) return null;
        var parsed = new DateTime(year, month, day, hour, minute, 0, utc ? DateTimeKind.Utc : DateTimeKind.Local)
            .AddMilliseconds(milliseconds);
        return new DateTimeOffset(utc ? parsed : parsed.ToUniversalTime()).ToUnixTimeMilliseconds();
    }
}
