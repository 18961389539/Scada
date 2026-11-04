using System.Net;
using System.Net.Sockets;

namespace Scada.Server.Mcp;

/// <summary>
/// MCP 端点没有登录态（设计上供局域网内的原生客户端调用），因此来源网段是唯一可用的边界。
/// 规则语义：
/// <list type="bullet">
/// <item>未配置（空数组）→ 只允许回环，即只有服务器本机的客户端与托盘/本机脚本能调用。</item>
/// <item>"*" → 不限制来源，只有部署方明确写出来才会生效。</item>
/// <item>其余按 CIDR 或单个地址匹配，IPv4 与 IPv6 不做隐式跨族匹配。</item>
/// </list>
/// </summary>
public static class McpSourceGuard
{
    public const string AnySource = "*";

    private static readonly string[] LoopbackOnly = ["127.0.0.1/32", "::1/128"];

    public static bool IsAllowed(IPAddress? remote, string[]? cidrs)
    {
        // 进程内调用（测试服务器、内置工具）没有网络来源，按本机处理。
        if (remote is null) return true;

        var address = Normalize(remote);
        var rules = cidrs is { Length: > 0 } ? cidrs : LoopbackOnly;
        foreach (var rule in rules)
        {
            var text = rule?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            if (text == AnySource) return true;
            if (TryMatch(address, text)) return true;
        }
        return false;
    }

    /// <summary>启动期校验规则写法，避免把笔误留到第一次请求才暴露。</summary>
    public static bool IsValidRule(string? rule)
    {
        var text = rule?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text == AnySource) return true;

        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? text : text[..slash], out var network)) return false;
        if (slash < 0) return true;
        return int.TryParse(text[(slash + 1)..], out var bits)
            && bits >= 0
            && bits <= (network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }

    // Kestrel 双栈监听时 IPv4 客户端会以 ::ffff:a.b.c.d 出现，先映射回 v4 再比较。
    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryMatch(IPAddress address, string rule)
    {
        var slash = rule.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? rule : rule[..slash], out var network)) return false;
        network = Normalize(network);
        // 不做隐式跨族比较：写了 v4 规则不会顺带放行 v6 来源，反之亦然。
        if (network.AddressFamily != address.AddressFamily) return false;

        var maxBits = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var bits = maxBits;
        if (slash >= 0 && !int.TryParse(rule[(slash + 1)..], out bits)) return false;
        if (bits is < 0 || bits > maxBits) return false;

        var expected = network.GetAddressBytes();
        var actual = address.GetAddressBytes();
        var whole = bits / 8;
        for (var i = 0; i < whole; i++)
        {
            if (expected[i] != actual[i]) return false;
        }

        var rest = bits % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (expected[whole] & mask) == (actual[whole] & mask);
    }
}
