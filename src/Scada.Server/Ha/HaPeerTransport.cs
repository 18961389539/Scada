using Scada.Engine.Maintenance;

namespace Scada.Server.Ha;

/// <summary>
/// 主备复制通道只认证、不加密。对端是非回环 HTTP 时给出与备份明文风险同级的健康告警。
/// 不默认强制全站 HTTPS，也不把 JWT 扫进密钥包。
/// </summary>
public static class HaPeerTransport
{
    public const string InsecureWarningCode = "ha_peer_unencrypted";

    public const string InsecureWarningMessage =
        "主备对等连接使用未加密的 HTTP（且不是本机回环）。密钥复制通道只认证、不加密，共享密钥一旦泄露即可拉取密钥环。生产环境应对等连接使用 HTTPS。此告警不改变默认监听，也不会强制全站 HTTPS。";

    public static SystemWarning? InsecureWarning(HaOptions options)
    {
        if (!options.Enabled) return null;
        return IsInsecurePeer(options.PeerUrl) ? new SystemWarning(InsecureWarningCode, InsecureWarningMessage) : null;
    }

    public static bool IsInsecurePeer(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback;
    }
}
