namespace Scada.Core.Security;

/// <summary>
/// 审计里记录查询串前的脱敏。手工构造匿名对象写 Details 时不会经过 <see cref="AuditDiff"/>
/// 的 [Sensitive] 差分，token / API Key / 签名等参数会原样进 runtime.db，并随备份与 HA 快照扩散。
/// 这里只保留参数名，把命中敏感规则的值换成 <c>***</c>，并对整串长度设上限。
/// </summary>
public static class AuditQuery
{
    private const int DefaultMaxLength = 512;

    private static readonly string[] SensitiveHints =
    [
        "token", "key", "secret", "password", "passwd", "pwd", "credential", "auth",
        "signature", "sig", "session", "cookie", "code", "ticket", "nonce", "salt", "hash"
    ];

    /// <summary>参数名（忽略大小写、去 URL 编码后）命中任一敏感片段即脱敏其值。</summary>
    public static bool IsSensitiveName(string name)
    {
        var decoded = Uri.UnescapeDataString(name);
        return SensitiveHints.Any(hint => decoded.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    public static string Scrub(string? queryString, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(queryString)) return string.Empty;
        var query = queryString.TrimStart('?');
        if (query.Length == 0) return string.Empty;
        // 攻击者可以用超长查询串把审计表撑大：先按 & 分段，再统一截断结果。
        var parts = new List<string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? null : pair[(separator + 1)..];
            parts.Add(value is null ? name : $"{name}={(IsSensitiveName(name) ? "***" : value)}");
            if (parts.Count >= 64) break;
        }
        var result = string.Join("&", parts);
        if (result.Length <= maxLength) return result;
        // 截断可能落在某个值的中间，补一个省略号让人看得出是被截断的。
        return result[..maxLength] + "…";
    }
}
