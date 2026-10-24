using Microsoft.AspNetCore.DataProtection;
using Scada.Core.Interfaces;

namespace Scada.Server.Services;

// 通知通道凭据加密：与设备口令共用持久化密钥环，但用独立 purpose，互不能解密。
public sealed class NotificationSecrets(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Scada.NotificationSecrets.v1");
    public string Protect(string plain) => _protector.Protect(plain);
    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

// 通知 HTTP 通道共用一个 HttpClient（单个请求的超时由通道配置控制）。
public sealed class NotificationHttp : IDisposable
{
    public HttpClient Client { get; } = new() { Timeout = Timeout.InfiniteTimeSpan };
    public void Dispose() => Client.Dispose();
}
