using System.Net;
using System.Text.Json;

namespace Scada.Gateway;

public sealed class GatewayHttpException(HttpStatusCode statusCode, string? code)
    : HttpRequestException($"网关中心返回 HTTP {(int)statusCode}", null, statusCode)
{ public string? Code { get; } = code; }

public static class GatewayHttp
{
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
        catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException) { }
        return new(response.StatusCode, code);
    }
}
