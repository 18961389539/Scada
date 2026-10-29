using System.Runtime.InteropServices;

namespace Scada.Server.Services;

// GUI 子系统（WinExe）双击启动没有控制台可看，启动失败必须给现场一个可见提示，
// 否则表现为“窗口一闪就没了”。服务方式运行时不弹窗，只依赖日志与服务恢复策略。
internal static class StartupErrorDialog
{
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void Show(string message, string caption = "SCADA 启动失败")
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return;
        if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()) return;
        try { MessageBoxW(IntPtr.Zero, message, caption, MbIconError | MbSetForeground); }
        catch (Exception) { }
    }
}