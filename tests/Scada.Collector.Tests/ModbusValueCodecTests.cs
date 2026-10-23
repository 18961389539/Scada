using System.Buffers.Binary;
using Scada.Collector.Modbus;
using Scada.Core.Enums;

namespace Scada.Collector.Tests;

public class ModbusValueCodecTests
{
    [Theory]
    [InlineData(ByteOrder.ABCD)]
    [InlineData(ByteOrder.BADC)]
    [InlineData(ByteOrder.CDAB)]
    [InlineData(ByteOrder.DCBA)]
    public void Float_RoundTripsForEachByteOrder(ByteOrder byteOrder)
    {
        var bytes = ModbusValueCodec.EncodeRegister(123.456, TagDataType.Float, byteOrder);
        var value = ModbusValueCodec.DecodeRegister(bytes, 0, TagDataType.Float, byteOrder);
        Assert.Equal(123.456, value, 3);
    }

    [Theory]
    [InlineData(ByteOrder.ABCD)]
    [InlineData(ByteOrder.BADC)]
    [InlineData(ByteOrder.CDAB)]
    [InlineData(ByteOrder.DCBA)]
    public void UInt32_RoundTripsForEachByteOrder(ByteOrder byteOrder)
    {
        var bytes = ModbusValueCodec.EncodeRegister(3_000_000_000, TagDataType.UInt32, byteOrder);
        var value = ModbusValueCodec.DecodeRegister(bytes, 0, TagDataType.UInt32, byteOrder);
        Assert.Equal(3_000_000_000, value);
    }

    [Fact]
    public void BigEndianFloat_DecodesFromWireBytes()
    {
        var payload = new byte[] { 0x42, 0xCE, 0x00, 0x00 };
        Assert.Equal(103, ModbusValueCodec.DecodeRegister(payload, 0, TagDataType.Float, ByteOrder.ABCD), 3);
    }

    [Fact]
    public void WordAndByteSwappedFloat_DecodesFromWireBytes()
    {
        var payload = new byte[] { 0x00, 0x00, 0x42, 0xCE };
        Assert.Equal(103, ModbusValueCodec.DecodeRegister(payload, 0, TagDataType.Float, ByteOrder.CDAB), 3);
    }

    [Fact]
    public void Int16_UInt16_DecodeBigEndian()
    {
        var payload = new byte[] { 0xFF, 0xFE, 0x12, 0x34 };
        Assert.Equal(-2, ModbusValueCodec.DecodeRegister(payload, 0, TagDataType.Int16, ByteOrder.ABCD));
        Assert.Equal(0x1234, ModbusValueCodec.DecodeRegister(payload, 1, TagDataType.UInt16, ByteOrder.ABCD));
    }

    [Fact]
    public void CoilBits_AreDecodedLittleBitFirst()
    {
        var payload = new byte[] { 0b0000_1010 };
        Assert.False(ModbusValueCodec.DecodeCoil(payload, 0));
        Assert.True(ModbusValueCodec.DecodeCoil(payload, 1));
        Assert.False(ModbusValueCodec.DecodeCoil(payload, 2));
        Assert.True(ModbusValueCodec.DecodeCoil(payload, 3));
    }

    [Fact]
    public void Double_RoundTrips()
    {
        var bytes = ModbusValueCodec.EncodeRegister(123456.789, TagDataType.Double, ByteOrder.ABCD);
        Assert.Equal(8, bytes.Length);
        Assert.Equal(123456.789, ModbusValueCodec.DecodeRegister(bytes, 0, TagDataType.Double, ByteOrder.ABCD), 3);
    }

    [Fact]
    public void Bool_EncodesLowRegisterByte()
    {
        var bytes = ModbusValueCodec.EncodeRegister(1, TagDataType.Bool, ByteOrder.ABCD);
        Assert.Equal(new byte[] { 0x00, 0x01 }, bytes);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(bytes));
    }

    [Theory]
    [InlineData(TagArea.Coil, 1)]
    [InlineData(TagArea.DiscreteInput, 2)]
    [InlineData(TagArea.HoldingRegister, 3)]
    [InlineData(TagArea.InputRegister, 4)]
    public void FunctionCodes_MatchAreas(TagArea area, byte expected) =>
        Assert.Equal(expected, ModbusValueCodec.FunctionCode(area));
}
