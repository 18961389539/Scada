using System.Buffers.Binary;
using Scada.Collector.Iec104;
using Scada.Core.Enums;
using Scada.Core.Options;

namespace Scada.Server.External.Slave;

/// <summary>
/// IEC 60870-5-104 信息元素编码。只覆盖两类点：单点遥信（M_SP_NA/TB）与短浮点遥测（M_ME_NC/TF）。
/// 归一化、标度化、累计量没有实现——它们需要测点量程与累计量语义，半成品比特型错误更难排查。
/// </summary>
public static class Iec104ServerCodec
{
    public static (byte TypeId, byte[] Element) Encode(ExportPointMap.IecSlot slot, ExportPointMap.ExportSample sample, bool withTime)
    {
        var quality = QualityBits(sample.Quality);
        if (slot.IsSinglePoint)
        {
            var siq = (byte)((sample.Value != 0 ? 1 : 0) | quality);
            if (!withTime) return (Iec104Types.M_SP_NA, [siq]);
            var element = new byte[8];
            element[0] = siq;
            Iec104Codec.Cp56(ToLocal(sample.Ts)).CopyTo(element, 1);
            return (Iec104Types.M_SP_TB, element);
        }

        var value = float.IsFinite((float)sample.Value) ? (float)sample.Value : 0f;
        if (!withTime)
        {
            var element = new byte[5];
            // 104 的短浮点是 IEEE 754 小端，和 Modbus 的大端寄存器语义相反。
            BinaryPrimitives.WriteSingleLittleEndian(element, value);
            element[4] = quality;
            return (Iec104Types.M_ME_NC, element);
        }
        else
        {
            var element = new byte[12];
            BinaryPrimitives.WriteSingleLittleEndian(element, value);
            element[4] = quality;
            Iec104Codec.Cp56(ToLocal(sample.Ts)).CopyTo(element, 5);
            return (Iec104Types.M_ME_TF, element);
        }
    }

    // QDS/SIQ 品质位：bit6 = NT（非当前值），bit7 = IV（无效）。
    private static byte QualityBits(TagQuality quality) => quality switch
    {
        TagQuality.Good => 0x00,
        TagQuality.Uncertain => 0x40,
        _ => 0x80
    };

    private static DateTime ToLocal(long ms) =>
        ms <= 0 ? DateTime.Now : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
}

/// <summary>点表里一个 IOA 的当前快照，用于变化检测。</summary>
public readonly record struct Iec104SampleState(double Value, TagQuality Quality)
{
    public bool Differs(Iec104SampleState other) =>
        Math.Abs(Value - other.Value) > 1e-9 || Quality != other.Quality;
}
