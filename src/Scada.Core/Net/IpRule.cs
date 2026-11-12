using System.Net;
using System.Net.Sockets;

namespace Scada.Core.Net;

/// <summary>
/// IP 白名单规则：单条为 "*"、单个地址或 CIDR。匹配前先把 IPv4-mapped IPv6 归一成 v4，
/// 但 v4 规则不会隐式匹配 v6 来源（反之亦然）。名单为空时是否放行由调用方决定。
/// </summary>
public static class IpRule
{
    public const string Any = "*";

    /// <summary>启动期校验规则写法，避免把笔误留到第一次连接才暴露。</summary>
    public static bool IsValid(string? rule)
    {
        var text = rule?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text == Any) return true;

        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? text : text[..slash], out var network)) return false;
        if (slash < 0) return true;
        return int.TryParse(text[(slash + 1)..], out var bits)
            && bits >= 0
            && bits <= (network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }

    public static bool IsMatch(IPAddress address, string? rule)
    {
        var text = rule?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text == Any) return true;

        var slash = text.IndexOf('/');
        if (!IPAddress.TryParse(slash < 0 ? text : text[..slash], out var network)) return false;
        address = Normalize(address);
        network = Normalize(network);
        // 不做隐式跨族比较：写了 v4 规则不会顺带放行 v6 来源，反之亦然。
        if (network.AddressFamily != address.AddressFamily) return false;

        var maxBits = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var bits = maxBits;
        if (slash >= 0 && !int.TryParse(text[(slash + 1)..], out bits)) return false;
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

    /// <summary>空名单 = 拒绝；调用方需要"空 = 全放行"时必须显式处理。</summary>
    public static bool IsAllowed(IPAddress? address, IReadOnlyCollection<string>? rules)
    {
        if (address is null || rules is not { Count: > 0 }) return false;
        foreach (var rule in rules)
        {
            if (IsMatch(address, rule)) return true;
        }
        return false;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
