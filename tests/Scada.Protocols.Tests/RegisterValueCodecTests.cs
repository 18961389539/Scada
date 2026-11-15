using Scada.Core.Enums;
using Scada.Protocols;

namespace Scada.Protocols.Tests;

public class RegisterValueCodecTests
{
    // 123456.789 的 IEEE 754 大端字节。下面几张表都是"设备真的这么发"的 wire 字节，
    // 而不是 Encode 出来再 Decode 回去——编码与解码共用同一张排列表，自编码自解码永远绿。
    private static readonly byte[] DoubleBigEndian = [0x40, 0xFE, 0x24, 0x0C, 0x9F, 0xBE, 0x76, 0xC9];

    [Theory]
    [InlineData(ByteOrder.ABCD, new byte[] { 0x40, 0xFE, 0x24, 0x0C, 0x9F, 0xBE, 0x76, 0xC9 })]
    [InlineData(ByteOrder.BADC, new byte[] { 0xFE, 0x40, 0x0C, 0x24, 0xBE, 0x9F, 0xC9, 0x76 })]
    [InlineData(ByteOrder.CDAB, new byte[] { 0x24, 0x0C, 0x40, 0xFE, 0x76, 0xC9, 0x9F, 0xBE })]
    [InlineData(ByteOrder.DCBA, new byte[] { 0xC9, 0x76, 0xBE, 0x9F, 0x0C, 0x24, 0xFE, 0x40 })]
    public void Double_UsesSameByteOrderSemanticsAs32Bit(ByteOrder byteOrder, byte[] wire)
    {
        Assert.Equal(wire, RegisterValueCodec.EncodeRegister(123456.789, TagDataType.Double, byteOrder));
        Assert.Equal(123456.789, RegisterValueCodec.DecodeRegister(wire, 0, TagDataType.Double, byteOrder), 6);
    }

    [Fact]
    public void DoubleLittleEndian_IsFullByteReverse()
    {
        // 现场最常见的小端 double：整段字节反序。DCBA 必须能读它，否则 64 位测点没有可用配置。
        var wire = DoubleBigEndian.Reverse().ToArray();
        Assert.Equal(123456.789, RegisterValueCodec.DecodeRegister(wire, 0, TagDataType.Double, ByteOrder.DCBA), 6);
        Assert.Equal(wire, RegisterValueCodec.EncodeRegister(123456.789, TagDataType.Double, ByteOrder.DCBA));
    }

    [Fact]
    public void Float_WordSwappedDecodesFromWireBytes()
    {
        // 103.0f = 42 CE 00 00（大端）；word swap 后是 00 00 42 CE
        Assert.Equal(103d, RegisterValueCodec.DecodeRegister([0x42, 0xCE, 0x00, 0x00], 0, TagDataType.Float, ByteOrder.ABCD), 3);
        Assert.Equal(103d, RegisterValueCodec.DecodeRegister([0x00, 0x00, 0x42, 0xCE], 0, TagDataType.Float, ByteOrder.CDAB), 3);
        Assert.Equal(103d, RegisterValueCodec.DecodeRegister([0xCE, 0x42, 0x00, 0x00], 0, TagDataType.Float, ByteOrder.BADC), 3);
        Assert.Equal(103d, RegisterValueCodec.DecodeRegister([0x00, 0x00, 0xCE, 0x42], 0, TagDataType.Float, ByteOrder.DCBA), 3);
    }

    [Theory]
    [InlineData(ByteOrder.ABCD)]
    [InlineData(ByteOrder.BADC)]
    [InlineData(ByteOrder.CDAB)]
    [InlineData(ByteOrder.DCBA)]
    public void AllRegisterTypes_RoundTrip(ByteOrder byteOrder)
    {
        foreach (var (type, value) in new (TagDataType, double)[]
                 {
                     (TagDataType.Int16, -12345), (TagDataType.UInt16, 54321), (TagDataType.Int32, -1234567),
                     (TagDataType.UInt32, 3_000_000_000), (TagDataType.Float, 12.5), (TagDataType.Double, 123456.789)
                 })
        {
            var bytes = RegisterValueCodec.EncodeRegister(value, type, byteOrder);
            Assert.Equal(RegisterValueCodec.ByteCount(type), bytes.Length);
            Assert.Equal(value, RegisterValueCodec.DecodeRegister(bytes, 0, type, byteOrder), 3);
        }
    }

    [Fact]
    public void Int16_UInt16_DecodeBigEndian()
    {
        var payload = new byte[] { 0xFF, 0xFE, 0x12, 0x34 };
        Assert.Equal(-2d, RegisterValueCodec.DecodeRegister(payload, 0, TagDataType.Int16, ByteOrder.ABCD));
        Assert.Equal(4660d, RegisterValueCodec.DecodeRegister(payload, 1, TagDataType.UInt16, ByteOrder.ABCD));
    }

    [Fact]
    public void Bool_IsLowRegisterByteBit0_AndIgnoresByteOrder()
    {
        Assert.Equal([0x00, 0x01], RegisterValueCodec.EncodeRegister(1, TagDataType.Bool, ByteOrder.ABCD));
        Assert.Equal([0x00, 0x01], RegisterValueCodec.EncodeRegister(1, TagDataType.Bool, ByteOrder.DCBA));
        Assert.Equal(1d, RegisterValueCodec.DecodeRegister([0x00, 0x01], 0, TagDataType.Bool, ByteOrder.ABCD));
        Assert.Equal(1d, RegisterValueCodec.DecodeRegister([0x00, 0xFF], 0, TagDataType.Bool, ByteOrder.ABCD));
        Assert.Equal(0d, RegisterValueCodec.DecodeRegister([0x01, 0x00], 0, TagDataType.Bool, ByteOrder.ABCD));
    }

    [Fact]
    public void CoilBits_AreDecodedLittleBitFirst()
    {
        var payload = new byte[] { 0b0000_1010 };
        Assert.False(RegisterValueCodec.DecodeCoil(payload, 0));
        Assert.True(RegisterValueCodec.DecodeCoil(payload, 1));
        Assert.False(RegisterValueCodec.DecodeCoil(payload, 2));
        Assert.True(RegisterValueCodec.DecodeCoil(payload, 3));
        Assert.False(RegisterValueCodec.DecodeCoil([0xFF, 0x00], 8));
        Assert.True(RegisterValueCodec.DecodeCoil([0xFF, 0x01], 8));
    }

    [Fact]
    public void ShortBlock_ThrowsWithContext_AndTryVariantReportsReason()
    {
        var payload = new byte[] { 0x42, 0xCE }; // 只有一个寄存器，Float 要两个
        var error = Assert.Throws<ProtocolFormatException>(() => RegisterValueCodec.DecodeRegister(payload, 0, TagDataType.Float, ByteOrder.ABCD));
        Assert.Contains("需要 4 字节", error.Message);

        Assert.False(RegisterValueCodec.TryDecodeRegister(payload, 0, TagDataType.Float, ByteOrder.ABCD, out _, out var reason));
        Assert.Contains("不足", reason);
        Assert.False(RegisterValueCodec.TryDecodeRegister(payload, 3, TagDataType.UInt16, ByteOrder.ABCD, out _, out _));
        Assert.False(RegisterValueCodec.TryDecodeRegister(payload, -1, TagDataType.UInt16, ByteOrder.ABCD, out _, out var negative));
        Assert.Contains("不能为负", negative);
        Assert.False(RegisterValueCodec.TryDecodeRegister(payload, 0, TagDataType.String, ByteOrder.ABCD, out _, out var text));
        Assert.Contains("文本", text);
    }

    [Theory]
    [InlineData(TagDataType.Int16, 32768)]
    [InlineData(TagDataType.Int16, -32769)]
    [InlineData(TagDataType.UInt16, -1)]
    [InlineData(TagDataType.UInt16, 65536)]
    [InlineData(TagDataType.Int32, 3e9)]
    [InlineData(TagDataType.UInt32, -1)]
    [InlineData(TagDataType.Float, 1e300)]
    [InlineData(TagDataType.Double, double.NaN)]
    [InlineData(TagDataType.Double, double.PositiveInfinity)]
    public void EncodeOutOfRange_ThrowsInsteadOfSaturating(TagDataType type, double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RegisterValueCodec.EncodeRegister(value, type, ByteOrder.ABCD));
        var destination = new byte[RegisterValueCodec.ByteCount(type)];
        Assert.False(RegisterValueCodec.TryEncodeRegister(value, type, ByteOrder.ABCD, destination, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void EncodeRoundsHalfAwayFromZero()
    {
        Assert.Equal(3d, RegisterValueCodec.DecodeRegister(RegisterValueCodec.EncodeRegister(2.5, TagDataType.Int16, ByteOrder.ABCD), 0, TagDataType.Int16, ByteOrder.ABCD));
        Assert.Equal(-3d, RegisterValueCodec.DecodeRegister(RegisterValueCodec.EncodeRegister(-2.5, TagDataType.Int16, ByteOrder.ABCD), 0, TagDataType.Int16, ByteOrder.ABCD));
    }

    [Fact]
    public void EncodeIntoTooSmallBuffer_Fails()
    {
        var destination = new byte[1];
        Assert.False(RegisterValueCodec.TryEncodeRegister(1, TagDataType.Float, ByteOrder.ABCD, destination, out var error));
        Assert.Contains("不足以容纳", error);
    }

    [Fact]
    public void String_UsesUtf8InRegisterOrder_AndTruncatesWholeRunes()
    {
        var bytes = RegisterValueCodec.EncodeString("SN-42", 8);
        Assert.Equal(8, bytes.Length);
        Assert.Equal("SN-42", RegisterValueCodec.DecodeString(bytes, 0, 8));
        // 5 字节装不下"序号A"（7 字节），退到能完整放下的字符
        Assert.Equal("序", RegisterValueCodec.DecodeString(RegisterValueCodec.EncodeString("序号A", 5), 0, 5));
        var block = new byte[] { 1, 2, 3, 4 }.Concat(RegisterValueCodec.EncodeString("AB", 4)).ToArray();
        Assert.Equal("AB", RegisterValueCodec.DecodeString(block, 2, 4));
        Assert.Equal("", RegisterValueCodec.DecodeString(block, 99, 4));
    }

    [Theory]
    [InlineData(TagArea.Coil, 1)]
    [InlineData(TagArea.DiscreteInput, 2)]
    [InlineData(TagArea.HoldingRegister, 3)]
    [InlineData(TagArea.InputRegister, 4)]
    public void FunctionCodes_MatchAreas(TagArea area, byte expected) =>
        Assert.Equal(expected, Modbus.ModbusFunctions.ForArea(area));

    [Fact]
    public void FunctionCodes_RejectNonRegisterAreas()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Modbus.ModbusFunctions.ForArea(TagArea.DataBlock));
        Assert.True(Modbus.ModbusFunctions.IsBitArea(TagArea.Coil));
        Assert.False(Modbus.ModbusFunctions.IsBitArea(TagArea.HoldingRegister));
    }
}
