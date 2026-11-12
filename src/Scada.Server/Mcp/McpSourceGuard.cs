using System.Net;
using Scada.Core.Net;

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
    public const string AnySource = IpRule.Any;

    private static readonly string[] LoopbackOnly = ["127.0.0.1/32", "::1/128"];

    public static bool IsAllowed(IPAddress? remote, string[]? cidrs)
    {
        // 进程内调用（测试服务器、内置工具）没有网络来源，按本机处理。
        if (remote is null) return true;

        var rules = cidrs is { Length: > 0 } ? cidrs : LoopbackOnly;
        foreach (var rule in rules)
        {
            if (IpRule.IsMatch(remote, rule)) return true;
        }
        return false;
    }

    /// <summary>启动期校验规则写法，避免把笔误留到第一次请求才暴露。</summary>
    public static bool IsValidRule(string? rule) => IpRule.IsValid(rule);
}
