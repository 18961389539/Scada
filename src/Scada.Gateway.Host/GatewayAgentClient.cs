using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Scada.Core.Models;

namespace Scada.Gateway.Hosting;

public sealed class GatewayAgentClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly GatewayAgentOptions _options;
    public GatewayAgentClient(GatewayAgentOptions options)
    {
        _options = options; _http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = options.Server, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Gateway", $"{options.GatewayId}:{options.Token}");
    }
    public async Task<GatewaySession> HelloAsync(Guid instance, Guid stream, CancellationToken ct) =>
        (await SendAsync<GatewaySession>(HttpMethod.Post, "hello", new GatewayHello(GatewayProtocol.Version, instance, stream, "1.0"), null, null, ct))!;
    public async Task<GatewaySession> HeartbeatAsync(Guid session, GatewayHeartbeat heartbeat, CancellationToken ct) =>
        (await SendAsync<GatewaySession>(HttpMethod.Post, "heartbeat", heartbeat, session, null, ct))!;
    public Task<GatewayConfiguration?> ConfigurationAsync(Guid session, string? revision, CancellationToken ct) =>
        SendAsync<GatewayConfiguration>(HttpMethod.Get, "configuration", null, session, revision, ct);
    public async Task GoodbyeAsync(Guid session, CancellationToken ct) =>
        _ = await SendAsync<GatewayAcknowledgement>(HttpMethod.Post, "goodbye", null, session, null, ct);
    public async Task<GatewayCommandOffers> CommandsAsync(Guid session, CancellationToken ct) =>
        (await SendAsync<GatewayCommandOffers>(HttpMethod.Get, "commands", null, session, null, ct))!;
    public async Task<GatewayCommandGrant> AuthorizeAsync(Guid session, Guid command, CancellationToken ct) =>
        (await SendAsync<GatewayCommandGrant>(HttpMethod.Post, $"commands/{command:D}/authorize", null, session, null, ct))!;
    public async Task<GatewayCommandReceiptAcknowledgement> ReportAsync(Guid session, GatewayCommandReceipt receipt, CancellationToken ct) =>
        (await SendAsync<GatewayCommandReceiptAcknowledgement>(HttpMethod.Post, "command-results", receipt, session, null, ct))!;
    private async Task<T?> SendAsync<T>(HttpMethod method, string route, object? body, Guid? session, string? revision, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_options.HttpTimeoutMs);
        using var request = new HttpRequestMessage(method, "api/gateway-agent/" + route);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, GatewayProtocol.Json), Encoding.UTF8, "application/json");
        if (session is not null) request.Headers.Add("X-Scada-Session", session.Value.ToString("D"));
        if (revision is not null) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{revision}\""));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotModified && route == "configuration" && revision is not null) return default;
        if (!response.IsSuccessStatusCode) throw await GatewayHttp.FailureAsync(response, deadline.Token);
        var bytes = await GatewayHttp.ReadBoundedAsync(response, route == "configuration" ? 32 * 1024 * 1024 : 4096, deadline.Token);
        return JsonSerializer.Deserialize<T>(bytes, GatewayProtocol.Json) ?? throw new InvalidDataException("中心响应缺少内容");
    }
    public void Dispose() => _http.Dispose();
}
