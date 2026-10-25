using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public sealed class OpcDaDeviceOptions
{
    /// <summary>OPC DA 服务器 ProgID，如 <c>Kepware.KEPServerEX.V6</c>、<c>Matrikon.OPC.Simulation.1</c>。</summary>
    public string ProgId { get; set; } = "";
    /// <summary>DCOM 远程节点（主机名/IP）；空 = 本机。</summary>
    public string Node { get; set; } = "";
    /// <summary>true = 从设备同步读（OPC_DS_DEVICE），false = 读服务器缓存（激活组，按更新周期刷新）。</summary>
    public bool ReadFromDevice { get; set; }
    public int UpdateRateMs { get; set; } = 1000;
    /// <summary>自动化接口返回的时间戳按 UTC 解释；false 按采集服务所在机器的本地时区解释。</summary>
    public bool TimestampsUtc { get; set; }
}

/// <summary>OPC DA 测点地址即服务器的 ItemID（如 <c>Channel1.Device1.Tag1</c>）。</summary>
public sealed partial class OpcDaSpec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.OpcDa;
    public bool UsesHostPort => false;
    public bool UsesCredentials => false;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.\-]{0,127}$")]
    private static partial Regex ProgIdPattern();
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,252})$")]
    private static partial Regex NodePattern();

    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<OpcDaDeviceOptions>(device, out var o) is string error) return error;
        if (!ProgIdPattern().IsMatch(o.ProgId)) return "须填写 OPC DA 服务器 ProgID（字母开头，仅字母数字 . _ -）";
        if (o.Node.Length > 0 && !NodePattern().IsMatch(o.Node)) return "远程节点须为主机名或 IP";
        if (o.UpdateRateMs is < 50 or > 3600000) return "更新周期须为 50～3600000 ms";
        return null;
    }

    public string? ValidateTag(Tag tag, Device? device)
    {
        var id = tag.NodeId;
        if (string.IsNullOrWhiteSpace(id) || id != id.Trim()) return "须填写 ItemID（首尾不能有空格）";
        if (id.Length > 512) return "ItemID 不能超过 512 字符";
        if (id.Any(char.IsControl)) return "ItemID 不能含控制字符";
        return null;
    }
}
