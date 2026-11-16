namespace Scada.Core.Options;

/// <summary>
/// 反向代理（TLS 终结）配置。默认关闭：服务端只认连接对端地址，限流按"每终端一个桶"
/// 分区的前提是能直接看到终端。放在 nginx/IIS 后面又不开这里，所有终端会共享一个代理地址，
/// 客户端配额合并成一份；确需代理时把代理地址列进 <see cref="KnownProxies"/>。
/// </summary>
public sealed class ReverseProxyOptions
{
    public bool Enabled { get; set; }

    /// <summary>可信代理的 IP 列表。只信任这些来源发来的 X-Forwarded-For / Proto。</summary>
    public string[] KnownProxies { get; set; } = [];
}
