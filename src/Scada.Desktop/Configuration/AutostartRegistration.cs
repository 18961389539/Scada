using Microsoft.Win32;

namespace Scada.Desktop.Configuration;

/// <summary>
/// 当前用户的登录自启项，不请求管理员权限。写入的是当前 exe 的绝对路径，
/// 移动安装目录后重新保存一次设置即可自愈。
/// </summary>
internal static class AutostartRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScadaDesktop";

    public static bool Apply(bool enabled, out string? warning)
    {
        warning = null;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            warning = "无法确定客户端程序路径，开机自启未修改";
            return false;
        }
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            warning = "开发环境（dotnet 宿主）不写入开机自启，请使用发布后的 Scada.Desktop.exe";
            return false;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                warning = "无法打开当前用户的开机自启注册表项";
                return false;
            }
            if (enabled) key.SetValue(ValueName, $"\"{executable}\" --supervise");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            DesktopLogger.Error("修改开机自启失败", exception);
            warning = "修改开机自启失败，请检查当前用户的注册表权限";
            return false;
        }
    }
}
