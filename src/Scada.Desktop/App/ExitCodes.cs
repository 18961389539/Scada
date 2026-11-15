namespace Scada.Desktop.App;

internal static class ExitCodes
{
    /// <summary>正常退出，守护进程随之退出。</summary>
    public const int Success = 0;

    /// <summary>未处理异常或 WebView2 崩溃，由守护进程重启。</summary>
    public const int Crash = 1;

    /// <summary>配置或认证致命失败，不自动重启，避免反复登录触发账号锁定。</summary>
    public const int Configuration = 2;

    /// <summary>WebView2 Evergreen Runtime 缺失。</summary>
    public const int WebViewMissing = 3;
}
