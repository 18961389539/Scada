using System.Buffers.Binary;
using Scada.Core.Enums;

namespace Scada.Collector.Modbus;

public static class ModbusValueCodec
{
    public static double DecodeRegister(ReadOnlySpan<byte> block, int registerOffset, TagDataType dataType, ByteOrder byteOrder)
    {
        var offset = registerOffset * 2;
        return dataType switch
        {
            TagDataType.Bool => block[offset + 1] & 0x01,
            TagDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(block.Slice(offset, 2)),
            TagDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(block.Slice(offset, 2)),
            TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(Order32(block.Slice(offset, 4), byteOrder)),
            TagDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(Order32(block.Slice(offset, 4), byteOrder)),
            TagDataType.Float => BinaryPrimitives.ReadSingleBigEndian(Order32(block.Slice(offset, 4), byteOrder)),
            TagDataType.Double => BinaryPrimitives.ReadDoubleBigEndian(Order64(block.Slice(offset, 8), byteOrder)),
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
        };
    }

    public static bool DecodeCoil(ReadOnlySpan<byte> block, int bitOffset) =>
        (block[bitOffset / 8] & (1 << (bitOffset % 8))) != 0;

    public static byte[] EncodeRegister(double value, TagDataType dataType, ByteOrder byteOrder)
    {
        switch (dataType)
        {
            case TagDataType.Bool:
            {
                var bytes = new byte[2];
                bytes[1] = value != 0 ? (byte)1 : (byte)0;
                return bytes;
            }
            case TagDataType.Int16:
            case TagDataType.UInt16:
            {
                var bytes = new byte[2];
                if (dataType == TagDataType.Int16)
                {
                    BinaryPrimitives.WriteInt16BigEndian(bytes, (short)Math.Round(value));
                }
                else
                {
                    BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)Math.Round(value));
                }

                return byteOrder == ByteOrder.BADC ? bytes.Reverse().ToArray() : bytes;
            }
            case TagDataType.Int32:
            case TagDataType.UInt32:
            case TagDataType.Float:
            {
                var bytes = new byte[4];
                if (dataType == TagDataType.Int32)
                {
                    BinaryPrimitives.WriteInt32BigEndian(bytes, (int)Math.Round(value));
                }
                else if (dataType == TagDataType.UInt32)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)Math.Round(value));
                }
                else
                {
                    BinaryPrimitives.WriteSingleBigEndian(bytes, (float)value);
                }

                return Unorder32(bytes, byteOrder);
            }
            case TagDataType.Double:
            {
                var bytes = new byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
                return Unorder64(bytes, byteOrder);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null);
        }
    }

    public static byte FunctionCode(TagArea area) => area switch
    {
        TagArea.Coil => 1,
        TagArea.DiscreteInput => 2,
        TagArea.HoldingRegister => 3,
        TagArea.InputRegister => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null)
    };

    private static byte[] Order32(ReadOnlySpan<byte> source, ByteOrder byteOrder)
    {
        var input = source.ToArray();
        return byteOrder switch
        {
            ByteOrder.ABCD => input,
            ByteOrder.BADC => [input[1], input[0], input[3], input[2]],
            ByteOrder.CDAB => [input[2], input[3], input[0], input[1]],
            ByteOrder.DCBA => [input[3], input[2], input[1], input[0]],
            _ => input
        };
    }

    private static byte[] Order64(ReadOnlySpan<byte> source, ByteOrder byteOrder)
    {
        var input = source.ToArray();
        return byteOrder switch
        {
            ByteOrder.ABCD => input,
            ByteOrder.BADC => [input[1], input[0], input[3], input[2], input[5], input[4], input[7], input[6]],
            ByteOrder.CDAB => [input[2], input[3], input[0], input[1], input[6], input[7], input[4], input[5]],
            ByteOrder.DCBA => [input[6], input[7], input[4], input[5], input[2], input[3], input[0], input[1]],
            _ => input
        };
    }

    private static byte[] Unorder32(byte[] bigEndian, ByteOrder byteOrder)
    {
        return byteOrder switch
        {
            ByteOrder.ABCD => bigEndian,
            ByteOrder.BADC => [bigEndian[1], bigEndian[0], bigEndian[3], bigEndian[2]],
            ByteOrder.CDAB => [bigEndian[2], bigEndian[3], bigEndian[0], bigEndian[1]],
            ByteOrder.DCBA => [bigEndian[3], bigEndian[2], bigEndian[1], bigEndian[0]],
            _ => bigEndian
        };
    }

    private static byte[] Unorder64(byte[] bigEndian, ByteOrder byteOrder)
    {
        return byteOrder switch
        {
            ByteOrder.ABCD => bigEndian,
            ByteOrder.BADC => [bigEndian[1], bigEndian[0], bigEndian[3], bigEndian[2], bigEndian[5], bigEndian[4], bigEndian[7], bigEndian[6]],
            ByteOrder.CDAB => [bigEndian[2], bigEndian[3], bigEndian[0], bigEndian[1], bigEndian[6], bigEndian[7], bigEndian[4], bigEndian[5]],
            ByteOrder.DCBA => [bigEndian[6], bigEndian[7], bigEndian[4], bigEndian[5], bigEndian[2], bigEndian[3], bigEndian[0], bigEndian[1]],
            _ => bigEndian
        };
    }
}
