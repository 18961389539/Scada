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
        var rules = cidrs is { Length: > 0 } ? cidrs : LoopbackOnly;
        // 没有来源地址不能按"本机"放行：反向代理的 ForwardedHeaders 配错、Unix 域套接字
        // 转发都会让所有外部调用落到这个分支，等于同时绕过网段、全局限流与分桶。
        // 只有部署方显式写出 "*" 才允许无来源地址（集成测试正是这样配置的）。
        if (remote is null) return Array.IndexOf(rules, AnySource) >= 0;

        foreach (var rule in rules)
        {
            if (IpRule.IsMatch(remote, rule)) return true;
        }
        return false;
    }

    /// <summary>启动期校验规则写法，避免把笔误留到第一次请求才暴露。</summary>
    public static bool IsValidRule(string? rule) => IpRule.IsValid(rule);
}
