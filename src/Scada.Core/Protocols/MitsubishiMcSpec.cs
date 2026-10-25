using System.Globalization;
using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public enum McFormat { Binary = 0, Ascii = 1 }
public enum IpTransport { Tcp = 0, Udp = 1 }

public sealed class McDeviceOptions
{
    public McFormat Format { get; set; } = McFormat.Binary;
    public IpTransport Transport { get; set; } = IpTransport.Tcp;
    public int NetworkNo { get; set; }
    public int PcNo { get; set; } = 0xFF;
    public int ModuleIo { get; set; } = 0x03FF;
    public int ModuleStation { get; set; }
    /// <summary>PLC 侧监视定时器，单位 250ms；0 表示无限等待（不推荐）。</summary>
    public int MonitoringTimer { get; set; } = 8;
    /// <summary>32/64 位值的字顺序：false = 低字在前（三菱默认）。</summary>
    public bool HighWordFirst { get; set; }
    /// <summary>批量读合并时允许的最大空隙（字/位）。</summary>
    public int MaxGap { get; set; } = 16;
}

/// <summary>三菱软元件定义。</summary>
public sealed record McDeviceKind(string Name, byte BinaryCode, string AsciiCode, bool IsBit, bool HexNumber);

/// <summary>MC 地址：<c>D100</c>、<c>D100.3</c>（字的位，只读）、<c>M10</c>、<c>X1F</c>（十六进制编号）、<c>ZR1000</c>。</summary>
public sealed partial record McAddress(McDeviceKind Device, int Number, int? Bit)
{
    public static readonly IReadOnlyDictionary<string, McDeviceKind> Devices = new McDeviceKind[]
    {
        new("SM", 0x91, "SM", true, false), new("SD", 0xA9, "SD", false, false),
        new("X", 0x9C, "X*", true, true), new("Y", 0x9D, "Y*", true, true),
        new("M", 0x90, "M*", true, false), new("L", 0x92, "L*", true, false), new("F", 0x93, "F*", true, false),
        new("V", 0x94, "V*", true, false), new("B", 0xA0, "B*", true, true),
        new("D", 0xA8, "D*", false, false), new("W", 0xB4, "W*", false, true),
        new("TS", 0xC1, "TS", true, false), new("TC", 0xC0, "TC", true, false), new("TN", 0xC2, "TN", false, false),
        new("SS", 0xC7, "SS", true, false), new("SC", 0xC6, "SC", true, false), new("SN", 0xC8, "SN", false, false),
        new("CS", 0xC4, "CS", true, false), new("CC", 0xC3, "CC", true, false), new("CN", 0xC5, "CN", false, false),
        new("SB", 0xA1, "SB", true, true), new("SW", 0xB5, "SW", false, true),
        new("DX", 0xA2, "DX", true, true), new("DY", 0xA3, "DY", true, true),
        new("R", 0xAF, "R*", false, false), new("ZR", 0xB0, "ZR", false, false), new("Z", 0xCC, "Z*", false, false),
    }.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^([A-Za-z]{1,2})([0-9A-Fa-f]+)(?:\.([0-9A-Fa-f]))?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string text, out McAddress address, out string? error)
    {
        address = null!; error = null;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) { error = "MC 地址格式应为 软元件+编号，如 D100、D100.3、M10、X1F、ZR1000"; return false; }
        // 两字母前缀优先（SM/SD/ZR/TN…），否则回退为单字母（如 “DX” 不会被误判为 D）。
        var prefix = m.Groups[1].Value;
        var digits = m.Groups[2].Value;
        if (!Devices.TryGetValue(prefix, out var kind))
        {
            if (prefix.Length == 2 && Devices.TryGetValue(prefix[..1], out kind) && kind.HexNumber) digits = prefix[1..] + digits;
            else { error = $"不支持的软元件“{prefix}”"; return false; }
        }
        var style = kind.HexNumber ? NumberStyles.HexNumber : NumberStyles.None;
        if (!int.TryParse(digits, style, CultureInfo.InvariantCulture, out var number) || number > 0xFFFFFF)
        { error = kind.HexNumber ? $"{kind.Name} 编号为十六进制（0～FFFFFF）" : $"{kind.Name} 编号为十进制（0～16777215）"; return false; }
        int? bit = null;
        if (m.Groups[3].Success)
        {
            if (kind.IsBit) { error = "位软元件不能再指定位号"; return false; }
            bit = int.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        address = new(kind, number, bit);
        return true;
    }

    public int Words(TagDataType type) => type switch
    {
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Double => 4,
        _ => 1
    };
}

public sealed class MitsubishiMcSpec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.MitsubishiMc;
    public bool UsesHostPort => true;
    public bool UsesCredentials => false;
    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<McDeviceOptions>(device, out var o) is string error) return error;
        if (!Enum.IsDefined(o.Format) || !Enum.IsDefined(o.Transport)) return "MC 报文格式或传输方式无效";
        if (o.NetworkNo is < 0 or > 255 || o.PcNo is < 0 or > 255 || o.ModuleIo is < 0 or > 0xFFFF || o.ModuleStation is < 0 or > 255) return "MC 网络号/PC 号/模块 IO/站号超出范围";
        if (o.MonitoringTimer is < 0 or > 0xFFFF) return "监视定时器须为 0～65535（单位 250ms）";
        if (o.MaxGap is < 0 or > 256) return "合并空隙须为 0～256";
        return null;
    }
    public string? ValidateTag(Tag tag, Device? device)
    {
        if (!McAddress.TryParse(tag.NodeId, out var a, out var error)) return error;
        if (a.Device.IsBit && tag.DataType != TagDataType.Bool) return "位软元件（X/Y/M/L/B…）只能使用 Bool 类型";
        if (a.Bit is not null && tag.DataType != TagDataType.Bool) return "字的位地址（如 D100.3）须为 Bool 类型";
        if (!a.Device.IsBit && a.Bit is null && tag.DataType == TagDataType.Bool) return "字软元件的 Bool 测点须指定位号，如 D100.3";
        if (a.Bit is not null && tag.Writable) return "字的位地址只读：写入需读-改-写整字，可能覆盖 PLC 同时修改的其他位";
        if ((long)a.Number + a.Words(tag.DataType) - 1 > 0xFFFFFF) return "地址加数据长度越界";
        return null;
    }
}
