using Scada.Desktop.Configuration;

namespace Scada.Desktop.Runtime;

internal static class TargetUrl
{
    public static string Build(DesktopSettings settings, string? serverUrl = null)
    {
        var root = (serverUrl ?? settings.ServerUrl).TrimEnd('/');
        return settings.Target.Kind switch
        {
            "screen" => AppendMonitorOnly($"{root}/kiosk/screens/{settings.Target.ScreenId}", settings.Target.MonitorOnly),
            "playlist" => AppendMonitorOnly($"{root}/kiosk/playlists/{settings.Target.PlaylistId}", settings.Target.MonitorOnly),
            "layout" => LayoutUrl(root, settings),
            "workstation" => $"{root}/console",
            "workspace" => $"{root}/workspace",
            _ => throw new InvalidOperationException("未知的显示目标类型")
        };
    }

    public static bool IsLoginPage(Uri uri) =>
        string.Equals(uri.AbsolutePath.TrimEnd('/'), "/login", StringComparison.OrdinalIgnoreCase);

    private static string LayoutUrl(string root, DesktopSettings settings)
    {
        var url = $"{root}/console/{settings.Target.LayoutId}";
        if (settings.Target.LayoutMonitorIndex is int index and >= 0)
            url += $"?monitor={index}";
        return url;
    }

    private static string AppendMonitorOnly(string url, bool monitorOnly) =>
        monitorOnly ? url + "?monitorOnly=1" : url;
}
