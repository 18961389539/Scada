using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Scada.Core.Models;

namespace Scada.Gateway;

public sealed class GatewayTelemetryUploader : IDisposable
{
    private readonly GatewayAgentStore _store;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly int _timeoutMs;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GatewayTelemetryUploader(GatewayAgentStore store, Uri server, int gatewayId, string token, Guid sessionId,
        bool allowLoopbackHttp = false, int timeoutMs = 15000)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0
            || server.Scheme != "https" && !(allowLoopbackHttp && server.Scheme == "http" && server.IsLoopback))
            throw new ArgumentException("网关中心地址必须使用 HTTPS；仅显式启用本地测试时允许回环 HTTP", nameof(server));
        if (gatewayId < 1 || string.IsNullOrEmpty(token) || token.Length is < 40 or > 256 || token.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')) || sessionId == Guid.Empty)
            throw new ArgumentException("网关身份或会话无效");
        if (timeoutMs is < 100 or > 60000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        _store = store; _timeoutMs = timeoutMs;
        _endpoint = new Uri(new Uri(server.AbsoluteUri.TrimEnd('/') + "/"), "api/gateway-agent/telemetry");
        _client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Gateway", $"{gatewayId}:{token}");
        _client.DefaultRequestHeaders.Add("X-Scada-Session", sessionId.ToString("D"));
    }

    // A single attempt always retains the packet unless an exact acknowledgement is received.
    public async Task<bool> UploadNextAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = await _store.PeekAsync(cancellationToken); if (pending is null) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(_timeoutMs);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(pending.Json, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Scada-Payload-Sha256", pending.Sha256);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"中心拒绝网关数据包（HTTP {(int)response.StatusCode}），队列数据仍保留", null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[4097]; var count = 0;
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token); if (read == 0) break; count += read;
            }
            if (count > 4096) throw new InvalidDataException("中心确认响应过大，队列数据仍保留");
            var acknowledgement = JsonSerializer.Deserialize<GatewayAcknowledgement>(buffer.AsSpan(0, count), GatewayProtocol.Json)
                ?? throw new InvalidDataException("中心未返回数据确认，队列数据仍保留");
            if (acknowledgement.StreamId != _store.StreamId || acknowledgement.Sequence != pending.Sequence)
                throw new InvalidDataException("中心确认的数据流或序号不匹配，队列数据仍保留");
            await _store.AcknowledgeAsync(acknowledgement, deadline.Token); return true;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _client.Dispose();
}
