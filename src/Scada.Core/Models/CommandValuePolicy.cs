using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Models;

public static class CommandValuePolicy
{
    public static bool TryGetRawValue(Tag tag, double engineering, out double raw, out string error)
    {
        raw = engineering;
        error = string.Empty;
        if (!double.IsFinite(engineering)) { error = "值无效"; return false; }
        if (tag.MinValue is double min && engineering < min || tag.MaxValue is double max && engineering > max)
        { error = "值超出测点允许范围"; return false; }
        if (tag.DataType == TagDataType.Bool)
        {
            if (engineering is not (0 or 1)) { error = "开关量只接受 0 或 1"; return false; }
            return true;
        }
        if (!double.IsFinite(tag.Scale) || tag.Scale == 0 || !double.IsFinite(tag.Offset))
        { error = "测点倍率或偏移无效，无法反算原始值"; return false; }
        raw = (engineering - tag.Offset) / tag.Scale;
        var (rawMin, rawMax) = tag.DataType switch
        {
            TagDataType.Int16 => ((double)short.MinValue, (double)short.MaxValue),
            TagDataType.UInt16 => (0d, (double)ushort.MaxValue),
            TagDataType.Int32 => ((double)int.MinValue, (double)int.MaxValue),
            TagDataType.UInt32 => (0d, (double)uint.MaxValue),
            TagDataType.Float => ((double)float.MinValue, (double)float.MaxValue),
            _ => (double.MinValue, double.MaxValue)
        };
        if (!double.IsFinite(raw) || raw < rawMin || raw > rawMax)
        { error = "工程量反算后超出设备数据类型范围"; return false; }
        if (tag.DataType is TagDataType.Int16 or TagDataType.UInt16 or TagDataType.Int32 or TagDataType.UInt32)
        {
            if (Math.Abs(raw - Math.Round(raw)) > 1e-7) { error = "工程量不能映射为整数寄存器值"; return false; }
            raw = Math.Round(raw);
        }
        return true;
    }

    // Float registers round the requested double to IEEE-754 single precision before transmission.
    // Compare to that encoded value; a relative tolerance could accept a materially different setpoint.
    public static bool Matches(Tag tag, double expectedRaw, double actualRaw) =>
        double.IsFinite(actualRaw) && actualRaw == (tag.DataType == TagDataType.Float ? (double)(float)expectedRaw : expectedRaw);
}
