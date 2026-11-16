namespace Scada.Desktop.Configuration;

internal static class AutostartDecision
{
    /// <summary>安装器已用计划任务托管时，客户端不能再写 HKCU Run，避免双启动。</summary>
    public static bool ShouldWriteRun(bool enabled, bool managedByInstaller) => enabled && !managedByInstaller;
}
