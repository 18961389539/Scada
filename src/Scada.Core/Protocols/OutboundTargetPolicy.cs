using System.Globalization;
using System.Net;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;

namespace Scada.Core.Protocols;

/// <summary>
/// 采集侧出站目标策略。现场最常见的安全事故不是被攻击，而是把设备地址填成公网地址、
/// 或从别处复制了一条公网 IP 的配置，采集器就此对外发起连接。
/// 默认只允许环回、私网、链路本地与唯一本地地址；公网目标必须显式放行。
///
/// 主机名（非 IP 字面量）在没有配置白名单时放行：生产现场普遍使用内网 DNS 名称，
/// 保存配置时做 DNS 解析既阻塞又不可靠（离线、超时、解析结果随时变化）。
/// 一旦配置了白名单，主机名也必须命中，等于切换到严格模式。
/// </summary>
public static class OutboundTargetPolicy
{
    public static string? Validate(string? host, ScadaSettings? settings)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var target = host.Trim();
        var allowlist = settings?.CollectorTargetAllowlist ?? [];
        if (Matches(allowlist, target)) return null;
        if (settings?.AllowPublicCollectorTargets == true) return null;
        if (IPAddress.TryParse(Unwrap(target), out var address))
        {
            return IsLocal(address)
                ? null
                : $"目标地址 {target} 属于公网范围，采集器默认只连接现场内网。确需连接请在 Scada:CollectorTargetAllowlist 中放行";
        }
        return allowlist.Count > 0
            ? $"目标主机 {target} 不在采集白名单内（Scada:CollectorTargetAllowlist）"
            : null;
    }

    /// <summary>按设备协议取出实际连接目标再校验；不使用 Host/Port 的协议（串口）直接放行。</summary>
    public static string? ValidateDevice(Device device, ScadaSettings? settings)
    {
        if (device.SimulationEnabled) return null;
        if (device.Protocol == ProtocolType.OpcUa)
            return Uri.TryCreate(device.OpcEndpointUrl, UriKind.Absolute, out var uri) ? Validate(uri.Host, settings) : null;
        if (ProtocolCatalog.TryGet(device.Protocol, out var spec)) return spec.UsesHostPort ? Validate(device.Host, settings) : null;
        // 没有独立协议规格的内建协议里，只有这三种走 Host/Port；串口协议不校验。
        return device.Protocol is ProtocolType.ModbusTcp or ProtocolType.ModbusRtuOverTcp or ProtocolType.SiemensS7
            ? Validate(device.Host, settings)
            : null;
    }

    /// <summary>环回、私网、链路本地、唯一本地与未指定地址视为现场内网。</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.Equals(IPAddress.IPv6Any);
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)
            || bytes[0] == 127
            || (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0);
    }

    private static bool Matches(IReadOnlyList<string> allowlist, string host)
    {
        foreach (var entry in allowlist)
        {
            var rule = entry?.Trim();
            if (string.IsNullOrEmpty(rule)) continue;
            if (rule == "*") return true;
            // *.example.com：允许任意子域，但不允许裸 example.com。
            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                if (host.EndsWith(rule[1..], StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (rule.Contains('/') && IPAddress.TryParse(Unwrap(host), out var hostAddress) && InRange(hostAddress, rule)) return true;
            if (string.Equals(rule, host, StringComparison.OrdinalIgnoreCase)) return true;
            if (IPAddress.TryParse(Unwrap(rule), out var ruleAddress) && IPAddress.TryParse(Unwrap(host), out var parsed)
                && ruleAddress.Equals(parsed)) return true;
        }
        return false;
    }

    private static bool InRange(IPAddress address, string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash <= 0 || slash == cidr.Length - 1) return false;
        if (!IPAddress.TryParse(Unwrap(cidr[..slash]), out var network)) return false;
        if (!int.TryParse(cidr[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var bits)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (network.IsIPv4MappedToIPv6) network = network.MapToIPv4();
        var actual = address.GetAddressBytes();
        var expected = network.GetAddressBytes();
        if (actual.Length != expected.Length || bits > actual.Length * 8) return false;
        var full = bits / 8;
        for (var i = 0; i < full; i++) if (actual[i] != expected[i]) return false;
        var rest = bits % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (actual[full] & mask) == (expected[full] & mask);
    }

    // Uri.Host 与部分配置会给 IPv6 加方括号，解析前去掉。
    private static string Unwrap(string host) => host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
}
