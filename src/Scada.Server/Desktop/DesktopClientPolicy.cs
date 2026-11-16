using Scada.Core.Options;
using Scada.Core.Versioning;

namespace Scada.Server.Desktop;

public sealed record DesktopClientPolicyView(
    string? MinimumVersion, string? LatestVersion, string? DownloadUrl, bool UpdateRequired, bool UpdateAvailable);

/// <summary>
/// 桌面客户端版本策略的唯一实现：心跳响应里的"必须升级/可升级"与在线列表里的"被阻塞"
/// 都从这里算，避免两处对同一个版本号给出不同结论。
///
/// 客户端上报的 BlockedReason 只作补充：被改过或版本过旧的客户端完全可以不报，
/// 运维页不能因此漏看"这台被门禁挡住了"。
/// </summary>
internal static class DesktopClientPolicy
{
    public static DesktopClientPolicyView Build(string? appVersion, DesktopClientOptions options)
    {
        var policy = Describe(options);
        return policy with
        {
            UpdateRequired = IsBlocked(appVersion, options),
            UpdateAvailable = AppVersionRule.IsBelow(appVersion, policy.LatestVersion)
        };
    }

    /// <summary>只描述策略本身（不含某个客户端的判定），用于运维页展示当前生效的门禁。</summary>
    public static DesktopClientPolicyView Describe(DesktopClientOptions options) =>
        new(NullIfEmpty(options.MinimumVersion),
            NullIfEmpty(options.LatestVersion),
            NormalizeDownloadUrl(options.DownloadUrl),
            UpdateRequired: false,
            UpdateAvailable: false);

    public static bool IsBlocked(string? appVersion, DesktopClientOptions options) =>
        AppVersionRule.IsBelow(appVersion, NullIfEmpty(options.MinimumVersion));

    public static bool IsOutdated(string? appVersion, DesktopClientOptions options) =>
        AppVersionRule.IsBelow(appVersion, NullIfEmpty(options.LatestVersion));

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeDownloadUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }
}
