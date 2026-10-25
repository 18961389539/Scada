using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public sealed class SparkplugDeviceOptions
{
    public string ClientId { get; set; } = "";
    public MqttVersion Version { get; set; } = MqttVersion.V500;
    public bool UseTls { get; set; }
    public bool AllowUntrustedCertificate { get; set; }
    public bool AllowPlaintextPassword { get; set; }
    public int KeepAliveSec { get; set; } = 30;
    public string GroupId { get; set; } = "";
    public string EdgeNodeId { get; set; } = "";
    /// <summary>为空表示测点属于边缘节点本身（NBIRTH/NDATA/NCMD），否则属于该设备（DBIRTH/DDATA/DCMD）。</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>主机应用 ID：非空时发布 spBv1.0/STATE/{id}（保留、遗嘱离线），作为 Primary Host。</summary>
    public string PrimaryHostId { get; set; } = "";
    /// <summary>检测到序号缺口或未收到出生证书时发送 NCMD Node Control/Rebirth。</summary>
    public bool RequestRebirth { get; set; } = true;
    public bool ConfirmWrites { get; set; } = true;
}

public static partial class SparkplugNames
{
    [GeneratedRegex(@"^[^/+#\s]{1,64}$")]
    private static partial Regex Id();
    public static bool ValidId(string id) => Id().IsMatch(id);
}

/// <summary>Sparkplug B 测点地址为度量名（metric name），如 <c>Inputs/Temperature</c>。</summary>
public sealed class SparkplugSpec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.SparkplugB;
    public bool UsesHostPort => true;
    public bool UsesCredentials => true;
    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<SparkplugDeviceOptions>(device, out var o) is string error) return error;
        if (!SparkplugNames.ValidId(o.GroupId) || !SparkplugNames.ValidId(o.EdgeNodeId)) return "Group ID 与 Edge Node ID 必填，1～64 个字符，不能含 / + # 或空白";
        if (o.DeviceId.Length > 0 && !SparkplugNames.ValidId(o.DeviceId)) return "Device ID 不能含 / + # 或空白";
        if (o.PrimaryHostId.Length > 0 && !SparkplugNames.ValidId(o.PrimaryHostId)) return "Primary Host ID 不能含 / + # 或空白";
        return MqttCommon.Validate(o.ClientId, 1, o.KeepAliveSec, 0, o.Version, o.UseTls, o.AllowUntrustedCertificate, o.AllowPlaintextPassword, device);
    }
    public string? ValidateTag(Tag tag, Device? device)
    {
        if (tag.NodeId.Length > 256 || tag.NodeId.Trim() != tag.NodeId || tag.NodeId.Any(char.IsControl)) return "度量名最多 256 字符，不能有首尾空白或控制字符";
        return null;
    }
}
