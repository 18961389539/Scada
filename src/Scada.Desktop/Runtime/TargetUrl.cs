using Scada.Desktop.Configuration;

namespace Scada.Desktop.Runtime;

internal static class TargetUrl
{
    public static string Build(DesktopSettings settings, string? serverUrl = null)
    {
        var root = (serverUrl ?? settings.ServerUrl).TrimEnd('/');
        return settings.Target.Kind switch
        {
            "screen" => $"{root}/kiosk/screens/{settings.Target.ScreenId}{(settings.Target.MonitorOnly ? "?monitorOnly=1" : string.Empty)}",
            "layout" => $"{root}/console/{settings.Target.LayoutId}",
            "workstation" => $"{root}/console",
            "workspace" => $"{root}/workspace",
            _ => throw new InvalidOperationException("未知的显示目标类型")
        };
    }

    public static bool IsLoginPage(Uri uri) =>
        string.Equals(uri.AbsolutePath.TrimEnd('/'), "/login", StringComparison.OrdinalIgnoreCase);
}
