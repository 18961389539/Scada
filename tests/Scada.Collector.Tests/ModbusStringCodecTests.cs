using Scada.Protocols.Modbus;

namespace Scada.Collector.Tests;

public sealed class ModbusStringCodecTests
{
    [Fact]
    public void Utf8_text_round_trips_and_is_zero_padded()
    {
        var bytes = ModbusValueCodec.EncodeString("SN-42", 8);
        Assert.Equal(8, bytes.Length);
        Assert.Equal("SN-42", ModbusValueCodec.DecodeString(bytes, 0, 8));
        Assert.All(bytes.Skip(5), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Encoding_stops_at_a_character_boundary()
    {
        // 每个汉字 3 字节：长度 5 只能放下一个汉字。
        var bytes = ModbusValueCodec.EncodeString("序号A", 5);
        Assert.Equal("序", ModbusValueCodec.DecodeString(bytes, 0, 5));
    }

    [Fact]
    public void Decode_uses_the_register_offset()
    {
        var block = new byte[] { 1, 2, 3, 4 }.Concat(ModbusValueCodec.EncodeString("AB", 4)).ToArray();
        Assert.Equal("AB", ModbusValueCodec.DecodeString(block, 2, 4));
    }
}
