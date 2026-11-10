using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;

namespace Scada.Core.Protocols;

public sealed class Iec61850DeviceOptions
{
    /// <summary>MMS 本地/远端 AP Title 等 ISO 参数保持默认即可；此处仅保留读取方式。</summary>
    public bool UseReports { get; set; }
}

/// <summary>
/// IEC 61850 数据属性引用：<c>逻辑设备/逻辑节点.功能约束.数据对象[.数据属性…]</c>，
/// 如 <c>IED1LD0/MMXU1.MX.TotW.mag.f</c>、<c>IED1CTRL/XCBR1.ST.Pos.stVal</c>、<c>IED1CTRL/CSWI1.CO.Pos</c>。
/// </summary>
public sealed partial record Iec61850Reference(string LogicalDevice, string LogicalNode, string Fc, string Path)
{
    public static readonly string[] FunctionalConstraints = ["ST", "MX", "SP", "SV", "CF", "DC", "SG", "SE", "SR", "OR", "BL", "EX", "CO"];

    [GeneratedRegex(@"^([A-Za-z0-9_]{1,64})/([A-Za-z0-9_]{1,32})\.([A-Z]{2})\.([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)*)$")]
    private static partial Regex Pattern();

    public static bool TryParse(string text, out Iec61850Reference reference, out string? error)
    {
        reference = null!; error = null;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) { error = "IEC 61850 引用格式应为 LD/LN.FC.DO[.DA…]，如 IED1LD0/MMXU1.MX.TotW.mag.f"; return false; }
        if (!FunctionalConstraints.Contains(m.Groups[3].Value)) { error = $"功能约束须为 {string.Join('/', FunctionalConstraints)}"; return false; }
        reference = new(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
        return true;
    }

    /// <summary>MMS 变量名：<c>LN$FC$DO$DA</c>，域名为逻辑设备。</summary>
    public string MmsItem => $"{LogicalNode}${Fc}${Path.Replace('.', '$')}";
}

public sealed class Iec61850Spec : IProtocolSpec
{
    public const string NotInstalledMessage =
        "未安装 IEC 61850 MMS 客户端插件：本产品不内置 MMS 栈（libiec61850 为 GPLv3，不能随产品分发）。" +
        "请将持有商业许可的 MMS 客户端打成驱动插件放入 Scada:DriverPluginRoot 后重启服务。未安装插件时仍可保存设备与测点做预配置。";
    public const string ReportsNotReadMessage = "使用报告尚未接入，当前版本不读取该选项。";

    public ProtocolType Protocol => ProtocolType.Iec61850;
    public bool UsesHostPort => true;
    public bool UsesCredentials => false;

    public string? ValidateDevice(Device device) => ValidateDevice(device, null);

    /// <summary>
    /// 只有当前宿主确认已加载 IEC 61850 插件时才允许启用采集，避免连接必失败的退避重连。
    /// 可用性来自注入的目录状态，而不是进程级静态开关。
    /// </summary>
    public string? ValidateDevice(Device device, IProtocolDriverAvailability? drivers)
    {
        if (ProtocolCatalog.TryOptions<Iec61850DeviceOptions>(device, out var options) is string error) return error;
        if (options.UseReports) return ReportsNotReadMessage;
        if (device.Enabled && !(drivers?.IsDriverAvailable(ProtocolType.Iec61850) ?? false)) return NotInstalledMessage;
        return null;
    }

    public string? ValidateTag(Tag tag, Device? device)
    {
        if (!Iec61850Reference.TryParse(tag.NodeId, out var r, out var error)) return error;
        if (tag.Writable && r.Fc is not ("CO" or "SP" or "SE" or "CF" or "DC")) return "只有 CO（控制）或 SP/SE/CF/DC 参数可写";
        if (!tag.Writable && r.Fc == "CO") return "CO 控制对象须设为可写";
        return null;
    }
}
