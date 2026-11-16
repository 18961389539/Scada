namespace Scada.Desktop.Configuration;

/// <summary>
/// 桌面客户端的本机数据目录。用户配置、日志和 WebView2 会话按 Windows 用户隔离；
/// 机器配置由安装器写入 ProgramData，只放非机密字段，供同一台机器的所有用户读取。
/// </summary>
internal sealed class DesktopPaths
{
    public DesktopPaths(string? root = null, string? machineRoot = null)
    {
        Root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scada", "Desktop")
            : Path.GetFullPath(root);
        MachineRoot = string.IsNullOrWhiteSpace(machineRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Scada", "Desktop")
            : Path.GetFullPath(machineRoot);
        SettingsFile = Path.Combine(Root, "settings.json");
        LogsDirectory = Path.Combine(Root, "logs");
        WebViewDirectory = Path.Combine(Root, "WebView2");
        MachineSettingsFile = Path.Combine(MachineRoot, "client.json");
        InstallManifestFile = Path.Combine(MachineRoot, "install.json");
        UserInstallManifestFile = Path.Combine(Root, "install.json");
    }

    public string Root { get; }

    public string MachineRoot { get; }

    public string SettingsFile { get; }

    public string MachineSettingsFile { get; }

    public string InstallManifestFile { get; }

    public string UserInstallManifestFile { get; }

    public string LogsDirectory { get; }

    public string WebViewDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(WebViewDirectory);
    }
}
