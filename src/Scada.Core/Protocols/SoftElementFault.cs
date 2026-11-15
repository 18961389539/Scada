using System.Globalization;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

/// <summary>从站拒绝某一段读时，坏点原因要能对上软元件和换算后的 Modbus 地址。</summary>
public static class SoftElementFault
{
    public static string Rejected(string? nodeId, TagArea area, int address, int length, int? bit, string reason)
    {
        var element = string.IsNullOrWhiteSpace(nodeId) ? "软元件" : nodeId.Trim();
        var where = area switch
        {
            TagArea.Coil => "线圈",
            TagArea.DiscreteInput => "离散输入",
            TagArea.InputRegister => "输入寄存器",
            TagArea.HoldingRegister => "保持寄存器",
            _ => area.ToString()
        };
        var range = length > 1
            ? $"{address.ToString(CultureInfo.InvariantCulture)}～{(address + length - 1).ToString(CultureInfo.InvariantCulture)}"
            : address.ToString(CultureInfo.InvariantCulture);
        var located = bit is int index
            ? $"{where} {range} 位 {index.ToString(CultureInfo.InvariantCulture)}"
            : $"{where} {range}";
        return $"{element} → {located}：{reason}";
    }
}
