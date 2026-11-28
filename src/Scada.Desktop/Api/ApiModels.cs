using System.Text.Json;

namespace Scada.Desktop.Api;

internal sealed record SiteInfo(string SiteName, bool DemoMode);

internal sealed record ScreenSummary(int Id, string? Name, int? PublishedRevision);

internal sealed record LayoutSummary(int Id, string? Name, int? PublishedRevision);

internal sealed record LayoutDetail(int Id, string? Name, int? PublishedRevision, LayoutDocument? Document);

internal sealed record LayoutDocument(List<LayoutMonitorInfo>? Monitors);

internal sealed record LayoutMonitorInfo(string? Name);

internal sealed record PlaylistSummary(int Id, string? Name, int[]? ScreenIds, int IntervalSeconds, bool MonitorOnly);

internal sealed record WorkstationSummary(int Id, string Name, int? LayoutId, bool AllowControl);

internal sealed record ResolvedWorkstation(WorkstationSummary Workstation, LayoutSummary? Layout);

/// <summary>登录成功后的会话。UserJson 保留原始 JSON 文本，供前端 localStorage 直接使用。</summary>
internal sealed record SessionData(string Token, string ExpiresAt, string UserJson, string? Locale, bool MustChangePassword)
{
    public static SessionData? Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String) return null;
            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token)) return null;
            if (!root.TryGetProperty("expiresAt", out var expiresElement) || expiresElement.ValueKind != JsonValueKind.String) return null;
            var expires = expiresElement.GetString();
            if (string.IsNullOrWhiteSpace(expires)) return null;
            if (!root.TryGetProperty("user", out var userElement) || userElement.ValueKind != JsonValueKind.Object) return null;

            var mustChange = userElement.TryGetProperty("mustChangePassword", out var mustChangeElement)
                && mustChangeElement.ValueKind == JsonValueKind.True;
            string? locale = null;
            if (userElement.TryGetProperty("locale", out var localeElement) && localeElement.ValueKind == JsonValueKind.String)
            {
                locale = localeElement.GetString();
            }
            return new SessionData(token!, expires!, userElement.GetRawText(), locale, mustChange);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal enum LoginStatus
{
    Success,
    InvalidCredentials,
    RateLimited,
    MustChangePassword,
    Network,
    Server,
    Protocol
}

internal sealed record LoginResult(LoginStatus Status, SessionData? Session, string Message, TimeSpan? RetryAfter = null, string? PrimaryUrl = null)
{
    public static LoginResult Success(SessionData session) =>
        session.MustChangePassword
            ? new LoginResult(LoginStatus.MustChangePassword, session, "该账号需要先修改初始密码，请先用浏览器登录并完成改密")
            : new LoginResult(LoginStatus.Success, session, string.Empty);
}

internal sealed class ApiException(string message, int? statusCode = null, string? primaryUrl = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;

    /// <summary>备机 503 响应里的主机地址；客户端据此切换。</summary>
    public string? PrimaryUrl { get; } = primaryUrl;
}

internal sealed record VersionPolicy(
    string? MinimumVersion, string? LatestVersion, string? DownloadUrl, bool UpdateRequired, bool UpdateAvailable);

internal sealed record HeartbeatPayload(
    string ClientId, string MachineName, string WindowsUser, string AppVersion, string? WebView2Version,
    string? BootId, string? TargetKind, int? TargetId, string? TargetName, string? ConfigScope, string? BlockedReason);

internal sealed record HeartbeatResult(
    bool Ok, long ServerTs, string? Node, string? HaRole, VersionPolicy? Policy, int HeartbeatSeconds);

internal sealed record HaRoleInfo(bool Enabled, string? Node, string? Role, string? PrimaryUrl);
