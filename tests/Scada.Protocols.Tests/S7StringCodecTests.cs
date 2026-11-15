using Scada.Protocols.S7;

namespace Scada.Protocols.Tests;

public class S7StringCodecTests
{
    [Fact]
    public void Encode_WritesLengthHeaderThenUtf8()
    {
        var bytes = S7StringCodec.Encode("ABC", 10);
        Assert.Equal(12, bytes.Length);
        Assert.Equal(10, bytes[0]);  // 最大长度
        Assert.Equal(3, bytes[1]);   // 实际长度
        Assert.Equal((byte)'A', bytes[2]);
        Assert.Equal((byte)'C', bytes[4]);
        Assert.Equal(0, bytes[5]);
        Assert.Equal("ABC", S7StringCodec.Decode(bytes, 10));
    }

    [Fact]
    public void Encode_TruncatesWholeRunes()
    {
        var bytes = S7StringCodec.Encode("序号A", 5); // 7 字节装不下
        Assert.Equal(5, bytes[0]);
        Assert.Equal(3, bytes[1]);
        Assert.Equal("序", S7StringCodec.Decode(bytes, 5));
    }

    [Fact]
    public void Decode_ClampsToDeclaredAndAvailableLength()
    {
        // 声明 10 字节、实际只带回 2 个字符：按可用部分解，不越界
        Assert.Equal("AB", S7StringCodec.Decode([10, 10, (byte)'A', (byte)'B']));
        // 完全不带的空串
        Assert.Equal("", S7StringCodec.Decode([10, 0]));
        Assert.Equal("", S7StringCodec.Decode([0x10]));
        // 测点配置长度小于 PLC 声明长度时按配置截断
        Assert.Equal("A", S7StringCodec.Decode([10, 2, (byte)'A', (byte)'B'], 1));
    }

    [Fact]
    public void Encode_RejectsImpossibleLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => S7StringCodec.Encode("A", 255));
        Assert.Throws<ArgumentOutOfRangeException>(() => S7StringCodec.Encode("A", -1));
        Assert.Equal(new byte[] { 0, 0 }, S7StringCodec.Encode("A", 0));
    }
}
