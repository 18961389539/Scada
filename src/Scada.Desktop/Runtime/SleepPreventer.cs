using System.Runtime.InteropServices;

namespace Scada.Desktop.Runtime;

/// <summary>
/// 无人值守大屏需要阻止系统休眠和关闭显示器。组策略仍可覆盖该请求，
/// 这里只做应用层尽力而为，不作为强制 kiosk 保证。
/// </summary>
internal static class SleepPreventer
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    public static void KeepAlive() => SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);

    public static void Release() => SetThreadExecutionState(EsContinuous);
}
