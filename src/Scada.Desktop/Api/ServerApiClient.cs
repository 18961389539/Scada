using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scada.Desktop.Api;

/// <summary>
/// 桌面客户端只调用现有只读接口：站点信息、登录、已发布画面/布局、工作站。
/// 所有错误都转换成中文可操作提示，不把原始 HTML 或堆栈抛到界面上。
/// </summary>
internal sealed class ServerApiClient : IHeartbeatClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public ServerApiClient(string serverUrl, HttpMessageHandler? handler = null)
    {
        var baseUri = new Uri(serverUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _http = handler is null ? new HttpClient(CreateHandler()) : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseUri;
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.Add("X-Scada-Locale", "zh-CN");
    }

    public Uri BaseAddress => _http.BaseAddress!;

    /// <summary>
    /// 终端只连一个内网服务端，不经过系统代理。企业镜像里常见的 HTTP_PROXY / WinINET 代理
    /// 会把 127.0.0.1 和车间网段的请求也转给代理，表现为"浏览器能打开、客户端连不上"；
    /// 网关侧此前也踩过同一个坑（回环地址绕开系统代理）。确需经代理访问服务端属于未支持的部署形态。
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    };

    public async Task<SiteInfo> GetSiteInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("api/auth/site-info", cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ApiException(ErrorMessage(body, "服务器信息读取失败"), (int)response.StatusCode);
            var info = JsonSerializer.Deserialize<SiteInfo>(body, Json);
            if (info is null || string.IsNullOrWhiteSpace(info.SiteName)) throw new ApiException("服务器地址不是可识别的 SCADA 服务端");
            return info;
        }
        catch (ApiException) { throw; }
        catch (JsonException) { throw new ApiException("服务器返回内容无法识别，请确认地址和端口"); }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken)) { throw new ApiException(DescribeNetworkFailure(exception)); }
    }

    public async Task<LoginResult> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http
                .PostAsJsonAsync("api/auth/login", new LoginRequest(userName, password), Json, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new LoginResult(LoginStatus.InvalidCredentials, null, ErrorMessage(body, "用户名或密码错误"));
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                return new LoginResult(LoginStatus.RateLimited, null, ErrorMessage(body, "登录失败次数过多，请稍后重试"), retryAfter);
            }
            if ((int)response.StatusCode >= 500)
            {
                return new LoginResult(LoginStatus.Server, null, ErrorMessage(body, "服务端暂时不可用"),
                    PrimaryUrl: PrimaryFrom(response, body));
            }
            if (!response.IsSuccessStatusCode)
            {
                return new LoginResult(LoginStatus.Protocol, null, ErrorMessage(body, $"登录失败（HTTP {(int)response.StatusCode}）"),
                    PrimaryUrl: PrimaryFrom(response, body));
            }
            var session = SessionData.Parse(body);
            if (session is null) return new LoginResult(LoginStatus.Protocol, null, "登录响应格式无法识别");
            return LoginResult.Success(session);
        }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
        {
            return new LoginResult(LoginStatus.Network, null, DescribeNetworkFailure(exception));
        }
        catch (JsonException)
        {
            return new LoginResult(LoginStatus.Protocol, null, "登录响应格式无法识别");
        }
    }

    public async Task<IReadOnlyList<ScreenSummary>> GetScreensAsync(string token, CancellationToken cancellationToken = default)
    {
        var rows = await GetAsync<List<ScreenSummary>>("api/screens", token, cancellationToken).ConfigureAwait(false);
        return rows.Where(row => row.PublishedRevision is > 0).ToList();
    }

    public async Task<IReadOnlyList<LayoutSummary>> GetLayoutsAsync(string token, CancellationToken cancellationToken = default)
    {
        var rows = await GetAsync<List<LayoutSummary>>("api/hmi/layouts", token, cancellationToken).ConfigureAwait(false);
        return rows.Where(row => row.PublishedRevision is > 0).ToList();
    }

    public async Task<IReadOnlyList<WorkstationSummary>> GetWorkstationsAsync(string token, CancellationToken cancellationToken = default)
    {
        return await GetAsync<List<WorkstationSummary>>("api/hmi/workstations", token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResolvedWorkstation> ResolveWorkstationAsync(int id, string token, CancellationToken cancellationToken = default)
    {
        return await GetAsync<ResolvedWorkstation>($"api/hmi/workstations/resolve?id={id}", token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HeartbeatResult> HeartbeatAsync(string token, HeartbeatPayload payload, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/desktop/heartbeat")
            {
                Content = JsonContent.Create(payload, options: Json)
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException(
                    ErrorMessage(body, $"心跳失败（HTTP {(int)response.StatusCode}）"),
                    (int)response.StatusCode,
                    PrimaryFrom(response, body));
            }
            var result = JsonSerializer.Deserialize<HeartbeatResult>(body, Json);
            if (result is null) throw new ApiException("心跳响应格式无法识别");
            return result;
        }
        catch (ApiException) { throw; }
        catch (JsonException) { throw new ApiException("心跳响应格式无法识别"); }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
        {
            throw new ApiException(DescribeNetworkFailure(exception));
        }
    }

    /// <summary>匿名读取 HA 角色；不可达时返回 null，由调用方按“未知”处理。</summary>
    public async Task<HaRoleInfo?> GetHaRoleAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync("api/ha/role", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<HaRoleInfo>(body, Json);
        }
        catch
        {
            return null;
        }
    }

    private async Task<T> GetAsync<T>(string path, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new ApiException("登录会话已失效，请重新登录", 401);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException(
                    ErrorMessage(body, $"读取数据失败（HTTP {(int)response.StatusCode}）"),
                    (int)response.StatusCode,
                    PrimaryFrom(response, body));
            }
            var value = JsonSerializer.Deserialize<T>(body, Json);
            if (value is null) throw new ApiException("服务器返回内容为空");
            return value;
        }
        catch (ApiException) { throw; }
        catch (JsonException) { throw new ApiException("服务器返回内容无法识别"); }
        catch (Exception exception) when (IsNetworkFailure(exception, cancellationToken))
        {
            throw new ApiException(DescribeNetworkFailure(exception));
        }
    }

    private static bool IsNetworkFailure(Exception exception, CancellationToken cancellationToken) =>
        (exception is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested;

    private static string DescribeNetworkFailure(Exception exception)
    {
        if (exception is TaskCanceledException) return "连接服务器超时，请检查网络或服务器状态";
        var current = exception;
        while (current is not null)
        {
            if (current is AuthenticationException) return "服务器证书不受信任，请先在客户端安装内部 CA 证书";
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound => "找不到服务器，请检查地址和 DNS 设置",
                    SocketError.ConnectionRefused => "服务器拒绝连接，请确认服务端已启动且端口正确",
                    SocketError.TimedOut => "连接服务器超时，请检查网络或服务器状态",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable => "网络不可达，请检查本机网络连接",
                    _ => $"无法连接服务器：{socket.Message}"
                };
            }
            current = current.InnerException;
        }
        return "无法连接服务器，请检查地址、端口和网络";
    }

    private static string? PrimaryFrom(HttpResponseMessage response, string body)
    {
        if (response.Headers.TryGetValues("X-Scada-Primary", out var values))
        {
            var header = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(header)) return header.Trim();
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("primaryUrl", out var primary)
                && primary.ValueKind == JsonValueKind.String)
            {
                var value = primary.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应没有可用的主机地址。
        }
        return null;
    }

    private static string ErrorMessage(string body, string fallback)
    {
        if (string.IsNullOrWhiteSpace(body)) return fallback;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 响应（通常是反代错误页）使用调用方给的兜底文案。
        }
        return fallback;
    }

    public void Dispose() => _http.Dispose();

    private sealed record LoginRequest(
        [property: JsonPropertyName("userName")] string UserName,
        [property: JsonPropertyName("password")] string Password);
}
