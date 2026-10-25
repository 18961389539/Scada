using System.Buffers.Binary;

namespace Scada.Collector.Iec104;

public enum ApciKind { I, S, U }

public static class Iec104Types
{
    public const byte M_SP_NA = 1, M_DP_NA = 3, M_ST_NA = 5, M_BO_NA = 7, M_ME_NA = 9, M_ME_NB = 11, M_ME_NC = 13, M_IT_NA = 15, M_ME_ND = 21;
    public const byte M_SP_TB = 30, M_DP_TB = 31, M_ST_TB = 32, M_BO_TB = 33, M_ME_TD = 34, M_ME_TE = 35, M_ME_TF = 36, M_IT_TB = 37;
    public const byte C_SC_NA = 45, C_DC_NA = 46, C_SE_NA = 48, C_SE_NB = 49, C_SE_NC = 50;
    public const byte M_EI_NA = 70, C_IC_NA = 100, C_CI_NA = 101, C_CS_NA = 103;
    public const byte CotSpont = 3, CotReq = 5, CotAct = 6, CotActCon = 7, CotDeact = 8, CotActTerm = 10, CotInrogen = 20, CotReqcogen = 37;
    public const byte U_StartDtAct = 0x07, U_StartDtCon = 0x0B, U_StopDtAct = 0x13, U_StopDtCon = 0x23, U_TestFrAct = 0x43, U_TestFrCon = 0x83;
}

public sealed record Apdu(ApciKind Kind, int SendSeq, int RecvSeq, byte UFunction, Asdu? Asdu);

/// <summary>ASDU（默认参数：COT 2 字节、公共地址 2 字节、信息对象地址 3 字节）。</summary>
public sealed record Asdu(byte TypeId, bool Sequence, byte Cause, bool Negative, bool Test, byte Originator, int CommonAddress, IReadOnlyList<InfoObject> Objects);

/// <summary>信息对象：地址 + 原始元素字节（含品质描述与时标）。</summary>
public sealed record InfoObject(int Ioa, byte[] Element);

public static class Iec104Codec
{
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
        Iec104Types.C_SC_NA or Iec104Types.C_DC_NA => 1,
        Iec104Types.C_SE_NA or Iec104Types.C_SE_NB => 3,
        Iec104Types.C_SE_NC => 5,
        Iec104Types.M_EI_NA => 1,
        Iec104Types.C_IC_NA or Iec104Types.C_CI_NA => 1,
        Iec104Types.C_CS_NA => 7,
        _ => -1
    };

    public static byte[] U(byte function) => [0x68, 4, function, 0, 0, 0];
    public static byte[] S(int recv) { var b = new byte[] { 0x68, 4, 1, 0, 0, 0 }; BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)(recv << 1)); return b; }

    public static byte[] I(int send, int recv, Asdu asdu)
    {
        var body = EncodeAsdu(asdu);
        if (body.Length > 249) throw new InvalidOperationException("ASDU 过长");
        var b = new byte[6 + body.Length];
        b[0] = 0x68; b[1] = (byte)(4 + body.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)(send << 1));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)(recv << 1));
        body.CopyTo(b, 6);
        return b;
    }

    public static byte[] EncodeAsdu(Asdu a)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(a.TypeId);
        ms.WriteByte((byte)((a.Sequence ? 0x80 : 0) | a.Objects.Count));
        ms.WriteByte((byte)(a.Cause | (a.Negative ? 0x40 : 0) | (a.Test ? 0x80 : 0)));
        ms.WriteByte(a.Originator);
        ms.WriteByte((byte)a.CommonAddress); ms.WriteByte((byte)(a.CommonAddress >> 8));
        for (var i = 0; i < a.Objects.Count; i++)
        {
            var o = a.Objects[i];
            if (!a.Sequence || i == 0) { ms.WriteByte((byte)o.Ioa); ms.WriteByte((byte)(o.Ioa >> 8)); ms.WriteByte((byte)(o.Ioa >> 16)); }
            ms.Write(o.Element);
        }
        return ms.ToArray();
    }

    /// <summary>TCP 流中判断完整 APDU 长度。</summary>
    public static int? FrameLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;
        if (data[0] != 0x68) throw new IOException("104 帧起始字节无效");
        if (data[1] < 4) throw new IOException("104 APDU 长度无效");
        return 2 + data[1];
    }

    public static Apdu Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || frame[0] != 0x68 || frame[1] != frame.Length - 2) throw new IOException("104 APDU 长度不一致");
        var c1 = frame[2];
        if ((c1 & 1) == 0)
        {
            var send = BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]) >> 1;
            var recv = BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]) >> 1;
            return new(ApciKind.I, send, recv, 0, DecodeAsdu(frame[6..]));
        }
        if ((c1 & 3) == 1) return new(ApciKind.S, 0, BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]) >> 1, 0, null);
        return new(ApciKind.U, 0, 0, c1, null);
    }

    public static Asdu DecodeAsdu(ReadOnlySpan<byte> d)
    {
        if (d.Length < 6) throw new IOException("ASDU 过短");
        var type = d[0]; var sq = (d[1] & 0x80) != 0; var n = d[1] & 0x7F;
        var cause = (byte)(d[2] & 0x3F); var negative = (d[2] & 0x40) != 0; var test = (d[2] & 0x80) != 0;
        var ca = d[4] | d[5] << 8;
        var len = ElementLength(type);
        var objects = new List<InfoObject>(n);
        if (len < 0) return new(type, sq, cause, negative, test, d[3], ca, objects); // 未知类型：保留头部以便应答
        var p = 6; var ioa = 0;
        for (var i = 0; i < n; i++)
        {
            if (!sq || i == 0)
            {
                if (p + 3 > d.Length) throw new IOException("ASDU 信息对象截断");
                ioa = d[p] | d[p + 1] << 8 | d[p + 2] << 16; p += 3;
            }
            else ioa++;
            if (p + len > d.Length) throw new IOException("ASDU 信息元素截断");
            objects.Add(new(ioa, d.Slice(p, len).ToArray())); p += len;
        }
        if (p != d.Length) throw new IOException("ASDU 长度与信息对象数量不符");
        return new(type, sq, cause, negative, test, d[3], ca, objects);
    }

    // ---- CP56Time2a ----
    public static byte[] Cp56(DateTime time)
    {
        var ms = time.Second * 1000 + time.Millisecond;
        return [(byte)ms, (byte)(ms >> 8), (byte)time.Minute, (byte)time.Hour, (byte)(time.Day | ((((int)time.DayOfWeek + 6) % 7 + 1) << 5)), (byte)time.Month, (byte)(time.Year - 2000)];
    }

    /// <summary>解析 CP56Time2a，无效（IV 位）或字段越界返回 null。</summary>
    public static long? Cp56Ms(ReadOnlySpan<byte> t, bool utc)
    {
        if (t.Length < 7 || (t[2] & 0x80) != 0) return null;
        var ms = t[0] | t[1] << 8; var minute = t[2] & 0x3F; var hour = t[3] & 0x1F; var day = t[4] & 0x1F; var month = t[5] & 0x0F; var year = 2000 + (t[6] & 0x7F);
        if (ms > 59999 || minute > 59 || hour > 23 || day is < 1 or > 31 || month is < 1 or > 12 || day > DateTime.DaysInMonth(year, month)) return null;
        var dt = new DateTime(year, month, day, hour, minute, 0, utc ? DateTimeKind.Utc : DateTimeKind.Local).AddMilliseconds(ms);
        return new DateTimeOffset(utc ? dt : dt.ToUniversalTime()).ToUnixTimeMilliseconds();
    }
}
