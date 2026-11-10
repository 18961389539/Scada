using System.Net;
using System.Text.Json;

namespace Scada.Gateway;

public sealed class GatewayHttpException(HttpStatusCode statusCode, string? code)
    : HttpRequestException($"网关中心返回 HTTP {(int)statusCode}", null, statusCode)
{ public string? Code { get; } = code; }

/// <summary>
/// 回环目标一律直连，其余目标沿用系统代理。
/// 任何代理都无法代替本机访问 127.0.0.1，而系统代理（HTTP_PROXY/HTTPS_PROXY）通常按主机名
/// 复用上游连接、忽略端口：一旦把回环请求交给代理，主备两个端口的流量会串到同一个套接字上，
/// 表现为「已经切换到主机，却仍然收到备机的 503」。
/// </summary>
public sealed class LoopbackBypassProxy(IWebProxy? fallback = null) : IWebProxy
{
    public ICredentials? Credentials
    {
        get => fallback?.Credentials;
        set { if (fallback is not null) fallback.Credentials = value; }
    }
    // 返回目标自身即「直连」，这样即使调用方忽略 IsBypassed 也不会误用代理。
    public Uri? GetProxy(Uri destination) => IsBypassed(destination) ? destination : fallback!.GetProxy(destination);
    public bool IsBypassed(Uri host) => host.IsLoopback || fallback is null || fallback.IsBypassed(host);
}

public static class GatewayHttp
{
    // 网关与中心（含主备两个地址）之间的全部 HTTP 都走这里：统一关掉重定向、回环直连。
    public static SocketsHttpHandler CreateHandler(bool allowAutoRedirect = false) =>
        new() { AllowAutoRedirect = allowAutoRedirect, Proxy = new LoopbackBypassProxy(HttpClient.DefaultProxy) };

    public static Uri Server(Uri server, bool allowLoopbackHttp)
    {
        if (!server.IsAbsoluteUri || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0
            || server.Scheme != "https" && !(allowLoopbackHttp && server.Scheme == "http" && server.IsLoopback))
            throw new ArgumentException("网关中心地址必须使用 HTTPS；仅显式启用本地测试时允许回环 HTTP");
        return new Uri(server.AbsoluteUri.TrimEnd('/') + "/");
    }
    public static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("中心响应超过大小限制");
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[Math.Min(limit + 1, 64 * 1024)];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        { if (output.Length + count > limit) throw new InvalidDataException("中心响应超过大小限制"); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    public static async Task<GatewayHttpException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // An explicit denial is authoritative even if its response body never completes.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(response.StatusCode, "identity_revoked");
        string? code = null;
        try
        {
            var bytes = await ReadBoundedAsync(response, 4096, ct); using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("code", out var property) && property.ValueKind == JsonValueKind.String)
            { var value = property.GetString(); if (value?.Length <= 64 && value.All(x => char.IsAsciiLetterOrDigit(x) || x == '_')) code = value; }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or OperationCanceledException) { }
        return new(response.StatusCode, code);
    }
}
