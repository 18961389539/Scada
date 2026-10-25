using System.Text.RegularExpressions;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public enum MqttVersion { V311 = 0, V500 = 1 }

public sealed class MqttDeviceOptions
{
    public string ClientId { get; set; } = "";
    public MqttVersion Version { get; set; } = MqttVersion.V500;
    public bool UseTls { get; set; }
    /// <summary>仅调试用：跳过服务器证书校验。</summary>
    public bool AllowUntrustedCertificate { get; set; }
    public int Qos { get; set; } = 1;
    public int KeepAliveSec { get; set; } = 30;
    public bool CleanSession { get; set; } = true;
    public bool RetainWrites { get; set; }
    /// <summary>写入后等待状态主题回报与写入值一致才算成功（否则报告“结果未知”）。</summary>
    public bool ConfirmWrites { get; set; } = true;
    /// <summary>超过此时长未收到新消息则质量为 Uncertain；0 表示不判定。</summary>
    public int StaleAfterMs { get; set; }
    /// <summary>允许在未启用 TLS 时发送密码（仅限隔离网络）。</summary>
    public bool AllowPlaintextPassword { get; set; }
}

/// <summary>
/// 通用 MQTT 测点地址：<c>状态主题[|$.json.path][;w=写入主题][;wpath=$.json.path][;ts=$.json.path]</c>。
/// 无 JSON 路径时载荷按纯文本数值/true/false 解析；写入载荷按 wpath 包装为 JSON，否则为纯文本数值。
/// </summary>
public sealed partial record MqttAddress(string Topic, string? Path, string? WriteTopic, string? WritePath, string? TimestampPath)
{
    public static bool TryParse(string text, out MqttAddress address, out string? error)
    {
        address = new("", null, null, null, null); error = null;
        var parts = text.Split(';');
        var head = parts[0].Split('|', 2);
        var topic = head[0].Trim();
        string? path = head.Length > 1 ? head[1].Trim() : null;
        string? write = null, wpath = null, ts = null;
        foreach (var part in parts.Skip(1))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) { error = $"地址参数“{part}”须为 键=值"; return false; }
            var value = kv[1].Trim();
            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "w": write = value; break;
                case "wpath": wpath = value; break;
                case "ts": ts = value; break;
                default: error = $"未知地址参数“{kv[0]}”（支持 w、wpath、ts）"; return false;
            }
        }
        if (!ValidTopic(topic)) { error = "状态主题无效：不能为空，不能包含通配符 + #，不能以 $ 开头"; return false; }
        if (write is not null && !ValidTopic(write)) { error = "写入主题无效：不能为空，不能包含通配符 + #"; return false; }
        foreach (var p in new[] { path, wpath, ts })
            if (p is not null && !JsonPath.IsValid(p)) { error = $"JSON 路径“{p}”无效，支持 $.a.b[0] 形式"; return false; }
        if (wpath is not null && write is null) { error = "wpath 须与 w 写入主题一起使用"; return false; }
        address = new(topic, path, write, wpath, ts);
        return true;
    }

    public static bool ValidTopic(string topic) => topic.Length is > 0 and <= 512 && !topic.Contains('+') && !topic.Contains('#') && !topic.StartsWith('$') && !topic.Contains('\0');
}

public static partial class JsonPath
{
    [GeneratedRegex(@"^\$(\.[A-Za-z0-9_\-]+|\[\d+\])+$")]
    private static partial Regex Pattern();
    [GeneratedRegex(@"\.([A-Za-z0-9_\-]+)|\[(\d+)\]")]
    private static partial Regex Segment();
    public static bool IsValid(string path) => path.Length <= 256 && Pattern().IsMatch(path);
    /// <summary>返回路径段：字符串为属性名，int 为数组下标。</summary>
    public static IReadOnlyList<object> Segments(string path) =>
        Segment().Matches(path).Select(m => m.Groups[1].Success ? (object)m.Groups[1].Value : int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
}

public sealed class MqttSpec : IProtocolSpec
{
    public ProtocolType Protocol => ProtocolType.Mqtt;
    public bool UsesHostPort => true;
    public bool UsesCredentials => true;
    public string? ValidateDevice(Device device)
    {
        if (ProtocolCatalog.TryOptions<MqttDeviceOptions>(device, out var o) is string error) return error;
        return MqttCommon.Validate(o.ClientId, o.Qos, o.KeepAliveSec, o.StaleAfterMs, o.Version, o.UseTls, o.AllowUntrustedCertificate, o.AllowPlaintextPassword, device);
    }
    public string? ValidateTag(Tag tag, Device? device)
    {
        if (!MqttAddress.TryParse(tag.NodeId, out var address, out var error)) return error;
        if (tag.Writable && address.WriteTopic is null) return "可写 MQTT 测点须用 ;w= 指定写入主题";
        return null;
    }
}

public static class MqttCommon
{
    public static string? Validate(string clientId, int qos, int keepAlive, int staleAfterMs, MqttVersion version, bool tls, bool untrusted, bool plaintextPassword, Device device)
    {
        if (clientId.Length > 128 || clientId.Any(char.IsControl)) return "客户端 ID 最多 128 个可见字符";
        if (qos is < 0 or > 2) return "QoS 须为 0、1 或 2";
        if (keepAlive is < 5 or > 3600) return "心跳间隔须为 5～3600 秒";
        if (staleAfterMs is < 0 or > 86_400_000) return "失效判定时长须为 0～86400000 ms";
        if (!Enum.IsDefined(version)) return "MQTT 版本无效";
        if (untrusted && !tls) return "跳过证书校验只能与 TLS 一起使用";
        if (!tls && !plaintextPassword && device.OpcPasswordProtected is not null) return "使用密码认证时须启用 TLS，避免明文传输口令（隔离网络可勾选“允许明文密码”）";
        return null;
    }
}
