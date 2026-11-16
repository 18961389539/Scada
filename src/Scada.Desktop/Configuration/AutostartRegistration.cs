using Microsoft.Win32;

namespace Scada.Desktop.Configuration;

/// <summary>
/// 当前用户的登录自启项，不请求管理员权限。安装器托管计划任务时不写这里，
/// 避免同一台机器出现两份自启；关闭时仍会清理历史遗留的 Run 值。
/// </summary>
internal static class AutostartRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ScadaDesktop";

    /// <summary>
    /// 应用开机自启设置。返回值而不是 <c>bool + out warning</c>：后者让调用方很容易
    /// 写成 <c>Apply(...) &amp;&amp; warning != null</c>，短路后失败分支永远不落日志。
    /// </summary>
    public static AutostartResult Apply(bool enabled, bool managedByInstaller)
    {
        if (managedByInstaller) return AutostartResult.Managed();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return AutostartResult.Failed("无法打开当前用户的开机自启注册表项");
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return AutostartResult.Success();
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                return AutostartResult.Failed("无法确定客户端程序路径，开机自启未修改");
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                return AutostartResult.Failed("开发环境（dotnet 宿主）不写入开机自启，请使用发布后的 Scada.Desktop.exe");

            key.SetValue(ValueName, $"\"{executable}\" --supervise");
            return AutostartResult.Success();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            DesktopLogger.Error("修改开机自启失败", exception);
            return AutostartResult.Failed("修改开机自启失败，请检查当前用户的注册表权限");
        }
    }
}

/// <summary>自启应用结果。<see cref="Warning"/> 非空即表示没写成功，调用方必须落日志。</summary>
internal readonly record struct AutostartResult(bool Handled, bool ManagedByInstaller, string? Warning)
{
    public static AutostartResult Success() => new(true, false, null);

    public static AutostartResult Managed() => new(true, true, null);

    public static AutostartResult Failed(string warning) => new(false, false, warning);
}
