using Scada.Protocols;
using Scada.Protocols.Iec104;
using static Scada.Protocols.Iec104.Iec104Types;

namespace Scada.Protocols.Tests;

public class Iec104CodecTests
{
    private static Asdu Single(byte type, byte cause, int ioa, params byte[] element) =>
        new(type, false, cause, false, false, 0, 7, [new InfoObject(ioa, element)]);

    [Fact]
    public void IFrame_MatchesWireBytes()
    {
        // 单点遥信 spontaneous，IOA 1001：68 0E | 0000 0000 | 01 01 03 00 07 00 | E9 03 00 | 01
        var frame = Iec104Codec.I(0, 0, Single(M_SP_NA, CotSpont, 1001, 0x01));
        Assert.Equal(new byte[]
        {
            0x68, 0x0E, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x01, 0x03, 0x00, 0x07, 0x00, 0xE9, 0x03, 0x00, 0x01
        }, frame);
    }

    [Fact]
    public void SequenceAsdu_WritesOnlyTheFirstAddress()
    {
        var asdu = new Asdu(M_SP_NA, true, CotSpont, false, false, 0, 7,
            [new InfoObject(1001, [0x00]), new InfoObject(1002, [0x01]), new InfoObject(1003, [0x00])]);
        var frame = Iec104Codec.I(0, 0, asdu);
        Assert.Equal(0x10, frame[1]);
        Assert.Equal(new byte[] { 0x01, 0x83, 0x03, 0x00, 0x07, 0x00, 0xE9, 0x03, 0x00, 0x00, 0x01, 0x00 },
            frame[6..]);

        var decoded = Iec104Codec.Decode(frame).Asdu!;
        Assert.True(decoded.Sequence);
        Assert.Equal(new[] { 1001, 1002, 1003 }, decoded.Objects.Select(x => x.Ioa));
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00 }, decoded.Objects.Select(x => x.Element[0]));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 3)]
    [InlineData(32767, 32766)]
    public void Apdu_RoundTripsSequenceNumbers(int send, int recv)
    {
        var apdu = Iec104Codec.Decode(Iec104Codec.I(send, recv, Single(M_ME_NC, CotSpont, 3003, 0x00, 0x00, 0x80, 0x3F, 0x00)));
        Assert.Equal(ApciKind.I, apdu.Kind);
        Assert.Equal(send, apdu.SendSeq);
        Assert.Equal(recv, apdu.RecvSeq);
        Assert.Equal(M_ME_NC, apdu.Asdu!.TypeId);
        Assert.Equal(3003, apdu.Asdu.Objects[0].Ioa);
    }

    [Fact]
    public void SAndUFrames_HaveFixedShape()
    {
        Assert.Equal(new byte[] { 0x68, 0x04, 0x01, 0x00, 0x0E, 0x00 }, Iec104Codec.S(7));
        Assert.Equal(new byte[] { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 }, Iec104Codec.U(U_StartDtAct));
        Assert.Equal(ApciKind.S, Iec104Codec.Decode(Iec104Codec.S(7)).Kind);
        Assert.Equal(ApciKind.U, Iec104Codec.Decode(Iec104Codec.U(U_TestFrCon)).Kind);
        Assert.Equal(U_TestFrCon, Iec104Codec.Decode(Iec104Codec.U(U_TestFrCon)).UFunction);
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.S(32768));
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.S(-1));
    }

    [Fact]
    public void WriteAsdu_RejectsValuesThatWouldBeSilentlyTruncated()
    {
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.EncodeAsdu(new Asdu(M_SP_NA, false, CotSpont, false, false, 0, 7,
            Enumerable.Range(0, 128).Select(i => new InfoObject(i + 1, [0x00])).ToArray())));
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.EncodeAsdu(new Asdu(M_SP_NA, false, 64, false, false, 0, 7,
            [new InfoObject(1, [0x00])])));
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.EncodeAsdu(new Asdu(M_SP_NA, false, CotSpont, false, false, 0, 70000,
            [new InfoObject(1, [0x00])])));
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.EncodeAsdu(new Asdu(M_SP_NA, false, CotSpont, false, false, 0, 7,
            [new InfoObject(0x1000000, [0x00])])));
        // 类型标识决定元素长度：M_SP_NA 是 1 字节，给 2 字节说明组帧代码写错了
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.EncodeAsdu(new Asdu(M_SP_NA, false, CotSpont, false, false, 0, 7,
            [new InfoObject(1, [0x00, 0x01])])));
    }

    [Fact]
    public void WriteAsdu_AcceptsExactlyMaxLength()
    {
        // 20 个 12 字节的 M_ME_TF，SQ=1：6 + 3 + 20*12 = 249，正好是 ASDU 上限
        var objects = Enumerable.Range(0, 20).Select(i => new InfoObject(1000 + i, new byte[12])).ToArray();
        var frame = Iec104Codec.I(0, 0, new Asdu(M_ME_TF, true, CotSpont, false, false, 0, 7, objects));
        Assert.Equal(255, frame.Length);
        Assert.Equal(253, frame[1]);
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.EncodeAsdu(
            new Asdu(M_ME_TF, true, CotSpont, false, false, 0, 7, objects.Append(new InfoObject(9999, new byte[12])).ToArray())));
    }

    [Fact]
    public void DecodeAsdu_MarksUnknownTypeInsteadOfPretendingItHasNoObjects()
    {
        var asdu = Iec104Codec.DecodeAsdu([0xFF, 0x01, 0x03, 0x00, 0x07, 0x00, 0xE9, 0x03, 0x00, 0x00]);
        Assert.True(asdu.Unsupported);
        Assert.Empty(asdu.Objects);
        Assert.False(Iec104Codec.IsSupportedType(0xFF));
        Assert.True(Iec104Codec.IsSupportedType(M_ME_TF));
    }

    [Fact]
    public void Decode_RejectsMalformedFrames()
    {
        // 长度字段与实际长度不符
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.Decode([0x68, 0x0E, 0x00, 0x00, 0x00, 0x00]));
        // 起始字节错误
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.Decode([0x69, 0x04, 0x01, 0x00, 0x00, 0x00]));
        // S 帧多带了一个字节：长度字段自洽，但不是合法 S 帧
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.Decode([0x68, 0x05, 0x01, 0x00, 0x0E, 0x00, 0x00]));
        // 信息对象地址截断
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.DecodeAsdu([0x01, 0x01, 0x03, 0x00, 0x07, 0x00, 0xE9]));
        // 多出一个字节：数量与长度对不上
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.DecodeAsdu([0x01, 0x01, 0x03, 0x00, 0x07, 0x00, 0xE9, 0x03, 0x00, 0x01, 0x00]));
        // ASDU 头不足 6 字节
        Assert.Throws<ProtocolFormatException>(() => Iec104Codec.DecodeAsdu([0x01, 0x01, 0x03]));
    }

    [Fact]
    public void Scan_ResynchronisesOnNoiseInsteadOfDroppingTheLink()
    {
        var frame = Iec104Codec.I(0, 0, Single(M_SP_NA, CotSpont, 1001, 0x01));
        var buffer = new byte[512];
        buffer[0] = 0x11; buffer[1] = 0x22; buffer[2] = 0x33;
        frame.CopyTo(buffer, 3);
        var count = 3 + frame.Length;

        Assert.Equal(FrameScan.Ready, Iec104Codec.Scan(buffer, ref count, out var length, out var skipped));
        Assert.Equal(frame.Length, length);
        Assert.Equal(3, skipped);
        Assert.Equal(frame, buffer.AsSpan(0, length).ToArray());

        // 帧前的假 APDU 头（0x68 + 非法长度 0x02）：两个字节都不是数据，丢掉后真帧照常切出来
        var fake = new byte[512];
        fake[0] = 0x68; fake[1] = 0x02;
        frame.CopyTo(fake, 2);
        count = 2 + frame.Length;
        Assert.Equal(FrameScan.Ready, Iec104Codec.Scan(fake, ref count, out length, out skipped));
        Assert.Equal(2, skipped);
        Assert.Equal(frame.Length, length);
    }

    [Fact]
    public void Scan_WaitsForMoreDataAndDiscardsPureNoise()
    {
        var frame = Iec104Codec.I(0, 0, Single(M_SP_NA, CotSpont, 1001, 0x01));
        var buffer = new byte[512];
        frame.CopyTo(buffer);
        var count = 5;
        Assert.Equal(FrameScan.NeedMore, Iec104Codec.Scan(buffer, ref count, out _, out _));
        Assert.Equal(5, count);

        var noise = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        count = noise.Length;
        Assert.Equal(FrameScan.NeedMore, Iec104Codec.Scan(noise, ref count, out _, out var skipped));
        Assert.Equal(0, count);
        Assert.Equal(4, skipped);
    }

    [Fact]
    public void Cp56_EncodesFieldLayoutAndRejectsOutOfRangeYears()
    {
        var time = new DateTime(2026, 11, 14, 22, 33, 16, 0, DateTimeKind.Utc); // 周六
        var bytes = Iec104Codec.Cp56(time);
        Assert.Equal(7, bytes.Length);
        Assert.Equal(new byte[] { 0x80, 0x3E, 0x21, 0x16, 0xCE, 0x0B, 0x1A }, bytes);
        Assert.Equal(14, bytes[4] & 0x1F);
        Assert.Equal(6, bytes[4] >> 5); // 周一=1 … 周日=7
        Assert.Equal(new DateTimeOffset(time).ToUnixTimeMilliseconds(), Iec104Codec.Cp56Ms(bytes, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => Iec104Codec.Cp56(new DateTime(1999, 12, 31)));

        var invalid = (byte[])bytes.Clone();
        invalid[2] |= 0x80;
        Assert.Null(Iec104Codec.Cp56Ms(invalid, true));
        Assert.Null(Iec104Codec.Cp56Ms([0x00, 0x00, 0x00, 0x00, 0x00, 0x0D, 0x1A], true)); // 13 月
    }
}
