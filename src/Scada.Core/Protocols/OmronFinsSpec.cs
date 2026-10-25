using System.Globalization;
using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public sealed class FinsDeviceOptions
{
    public IpTransport Transport { get; set; } = IpTransport.Udp;
    public int DestNetwork { get; set; }
    /// <summary>PLC 节点号。UDP 必填（通常等于 PLC IP 末段）；TCP 为 0 时采用握手返回的服务器节点号。</summary>
    public int DestNode { get; set; }
    public int DestUnit { get; set; }
    public int SourceNetwork { get; set; }
    /// <summary>本机节点号。UDP 必填且在该网络内唯一；TCP 为 0 时由 PLC 自动分配。</summary>
    public int SourceNode { get; set; }
    public bool HighWordFirst { get; set; }
    public int MaxGap { get; set; } = 16;
}

public sealed record FinsArea(string Name, byte WordCode, byte? BitCode, int MaxAddress);

/// <summary>FINS 地址：<c>DM100</c>、<c>D100</c>、<c>CIO10.05</c>、<c>W3.00</c>、<c>H10</c>、<c>A100</c>、<c>T5</c>（定时器当前值）。</summary>
public sealed partial record FinsAddress(FinsArea Area, int Word, int? Bit)
{
    public static readonly IReadOnlyDictionary<string, FinsArea> Areas = new Dictionary<string, FinsArea>(StringComparer.OrdinalIgnoreCase)
    {
        ["CIO"] = new("CIO", 0xB0, 0x30, 6143), ["WR"] = new("WR", 0xB1, 0x31, 511), ["W"] = new("WR", 0xB1, 0x31, 511),
        ["HR"] = new("HR", 0xB2, 0x32, 1535), ["H"] = new("HR", 0xB2, 0x32, 1535), ["AR"] = new("AR", 0xB3, 0x33, 959), ["A"] = new("AR", 0xB3, 0x33, 959),
        ["DM"] = new("DM", 0x82, 0x02, 32767), ["D"] = new("DM", 0x82, 0x02, 32767),
        ["TIM"] = new("TIM", 0x89, null, 4095), ["T"] = new("TIM", 0x89, null, 4095), ["CNT"] = new("CNT", 0x89, null, 4095),
    };

    [GeneratedRegex(@"^([A-Za-z]{1,3})(\d{1,5})(?:\.(\d{1,2}))?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string text, out FinsAddress address, out string? error)
    {
        address = null!; error = null;
        var m = Pattern().Match(text.Trim());
        if (!m.Success || !Areas.TryGetValue(m.Groups[1].Value, out var area)) { error = "FINS 地址格式应为 区域+字地址[.位]，区域：CIO/W/H/A/D(DM)/T(TIM)/CNT"; return false; }
        var word = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        // 计数器当前值与定时器共用区域码 0x89，计数器地址偏移 0x8000。
        if (area.Name == "CNT") word += 0x8000;
        if (word > (area.Name == "CNT" ? 0x8000 + area.MaxAddress : area.MaxAddress)) { error = $"{area.Name} 地址超出 0～{area.MaxAddress}"; return false; }
        int? bit = null;
        if (m.Groups[3].Success)
        {
            if (area.BitCode is null) { error = $"{area.Name} 不支持位地址"; return false; }
            bit = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (bit > 15) { error = "位号须为 0～15"; return false; }
        }
        address = new(area, word, bit);
        return true;
    }
}

public sealed class OmronFinsSpec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.OmronFins;
    public bool UsesHostPort => true;
    public bool UsesCredentials => false;
    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<FinsDeviceOptions>(device, out var o) is string error) return error;
        if (!Enum.IsDefined(o.Transport)) return "FINS 传输方式无效";
        if (o.DestNetwork is < 0 or > 127 || o.SourceNetwork is < 0 or > 127) return "FINS 网络号须为 0～127";
        if (o.DestNode is < 0 or > 254 || o.SourceNode is < 0 or > 254 || o.DestUnit is < 0 or > 255) return "FINS 节点号须为 0～254，单元号 0～255";
        if (o.Transport == IpTransport.Udp && (o.DestNode == 0 || o.SourceNode == 0)) return "FINS/UDP 须指定 PLC 节点号与本机节点号（1～254，本机节点号在网络内唯一）";
        if (o.Transport == IpTransport.Udp && o.DestNode == o.SourceNode && o.DestNetwork == o.SourceNetwork) return "本机节点号不能与 PLC 相同";
        if (o.MaxGap is < 0 or > 256) return "合并空隙须为 0～256";
        return null;
    }
    public string? ValidateTag(Tag tag, Device? device)
    {
        if (!FinsAddress.TryParse(tag.NodeId, out var a, out var error)) return error;
        if (a.Bit is not null && tag.DataType != TagDataType.Bool) return "位地址须为 Bool 类型";
        if (a.Bit is null && tag.DataType == TagDataType.Bool) return "Bool 测点须指定位号，如 CIO10.05";
        var words = tag.DataType switch { TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2, TagDataType.Double => 4, _ => 1 };
        if ((a.Word & 0x7FFF) + words - 1 > a.Area.MaxAddress) return "地址加数据长度越界";
        return null;
    }
}
