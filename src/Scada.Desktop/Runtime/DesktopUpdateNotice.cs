using Scada.Desktop.Api;

namespace Scada.Desktop.Runtime;

/// <summary>
/// 可升级提示的文案与链接。抽出来是为了能单测：现场只有"配了 LatestVersion 但没人知道"
/// 这一个失败模式，判定不该埋在 WinForms 事件里。
/// </summary>
internal static class DesktopUpdateNotice
{
    public static bool TryCreate(VersionPolicy? policy, out string message, out string? url)
    {
        message = string.Empty;
        url = null;
        if (policy is null || string.IsNullOrWhiteSpace(policy.LatestVersion)) return false;

        if (!string.IsNullOrWhiteSpace(policy.DownloadUrl)
            && Uri.TryCreate(policy.DownloadUrl.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            url = parsed.ToString();
        }

        message = url is null
            ? $"有新版本 {policy.LatestVersion} 可用，请联系管理员更新"
            : $"有新版本 {policy.LatestVersion} 可用，点击此处下载";
        return true;
    }
}
