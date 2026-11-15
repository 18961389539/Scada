namespace Scada.Desktop.Configuration;

/// <summary>
/// 桌面客户端的本机数据目录。配置、日志和 WebView2 会话都按 Windows 用户隔离，
/// 不写入程序目录，避免升级或移动安装位置时丢配置。
/// </summary>
internal sealed class DesktopPaths
{
    public DesktopPaths(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scada", "Desktop")
            : Path.GetFullPath(root);
        SettingsFile = Path.Combine(Root, "settings.json");
        LogsDirectory = Path.Combine(Root, "logs");
        WebViewDirectory = Path.Combine(Root, "WebView2");
    }

    public string Root { get; }

    public string SettingsFile { get; }

    public string LogsDirectory { get; }

    public string WebViewDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(WebViewDirectory);
    }
}
