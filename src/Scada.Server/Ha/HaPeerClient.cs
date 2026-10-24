using System.Net.Http.Json;

namespace Scada.Server.Ha;

public sealed record HaPeerStatus(string NodeId, string Role, long Term, string? SelfUrl, long Ts);
public sealed record HaHandoverRequest(string From, long Term, string Actor, string Reason);
public sealed record HaForwardRequest(string Actor, string Reason);

// 节点间调用：共享密钥放在请求头，只走 /api/ha/peer/*。
public sealed class HaPeerClient : IDisposable
{
    public const string KeyHeader = "X-Scada-Ha-Key";
    private readonly HttpClient _http;
    private readonly HaOptions _options;

    public HaPeerClient(HaOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(options.PeerUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add(KeyHeader, options.SharedKey);
    }

    private CancellationTokenSource Deadline(CancellationToken ct, int? ms = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(ms ?? _options.PeerTimeoutMs); return cts;
    }

    public async Task<HaPeerStatus?> StatusAsync(CancellationToken ct)
    {
        try
        {
            using var cts = Deadline(ct);
            return await _http.GetFromJsonAsync<HaPeerStatus>("api/ha/peer/status", HaReplication.Json, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested) { return null; }
    }

    public async Task<HaManifest> ManifestAsync(CancellationToken ct)
    {
        using var cts = Deadline(ct, Math.Max(_options.PeerTimeoutMs, 10000));
        return (await _http.GetFromJsonAsync<HaManifest>("api/ha/peer/manifest", HaReplication.Json, cts.Token))!;
    }

    public async Task DownloadConfigAsync(string target, CancellationToken ct)
    {
        using var cts = Deadline(ct, 120000);
        using var response = await _http.GetAsync("api/ha/peer/config", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(target);
        await response.Content.CopyToAsync(file, cts.Token);
    }

    public async Task<HaHistoryRows> HistoryAsync(string file, string table, long after, CancellationToken ct)
    {
        using var cts = Deadline(ct, 60000);
        return (await _http.GetFromJsonAsync<HaHistoryRows>($"api/ha/peer/history?file={Uri.EscapeDataString(file)}&table={table}&after={after}&limit={_options.HistoryBatchRows}", HaReplication.Json, cts.Token))!;
    }

    public async Task PostAsync(string route, object body, int timeoutMs, CancellationToken ct)
    {
        using var cts = Deadline(ct, timeoutMs);
        using var response = await _http.PostAsJsonAsync("api/ha/peer/" + route, body, HaReplication.Json, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cts.Token);
            string? message = null;
            try { message = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("message").GetString(); } catch { }
            throw new HaException(message ?? $"对端返回 HTTP {(int)response.StatusCode}", 502);
        }
    }

    public void Dispose() => _http.Dispose();
}
