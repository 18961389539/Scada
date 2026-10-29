using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Scada.Server.Services;

// 绿色包双击运行时没有窗口、控制台和浏览器标签，托盘图标是唯一可见的运行入口。
// 只用 Win32 API（Shell_NotifyIcon），不引入 WinForms/WPF，保持 net10.0 可跨平台发布；
// 非 Windows、服务模式、开发/自动化环境不会创建。
internal sealed class TrayIcon : IDisposable
{
    private const uint WM_APP = 0x8000;
    private const uint WM_TRAY = WM_APP + 1;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_NULL = 0x0000;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;

    private const uint MF_STRING = 0x0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int ID_OPEN = 1;
    private const int ID_LOGS = 2;
    private const int ID_EXIT = 3;

    private static readonly IntPtr HwndMessage = new(-3);
    private const int IdiApplication = 32512;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;

    private readonly string _url;
    private readonly string _logRoot;
    private readonly string _tip;
    private readonly Action _exit;
    private readonly ILogger _logger;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _ownsIcon;
    private uint _taskbarCreated;
    private WndProcDelegate? _wndProc;

    private TrayIcon(string url, string logRoot, string tip, Action exit, ILogger logger)
    {
        _url = url;
        _logRoot = logRoot;
        _tip = tip.Length > 120 ? tip[..120] : tip;
        _exit = exit;
        _logger = logger;
    }

    public static TrayIcon Start(string url, string logRoot, Action exit, ILogger logger)
    {
        var tray = new TrayIcon(url, logRoot, $"SCADA · {url}", exit, logger);
        tray._thread = new Thread(tray.Run) { IsBackground = true, Name = "scada-tray" };
        if (OperatingSystem.IsWindows()) tray._thread.SetApartmentState(ApartmentState.STA);
        tray._thread.Start();
        // 窗口创建失败时 Run 也会 Set，避免阻塞启动流程。
        tray._ready.Wait(TimeSpan.FromSeconds(5));
        return tray;
    }

    private void Run()
    {
        try
        {
            var instance = GetModuleHandleW(null);
            _wndProc = WndProc;
            var windowClass = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = "ScadaTrayWindow"
            };
            RegisterClassExW(ref windowClass);
            _hwnd = CreateWindowExW(0, "ScadaTrayWindow", "", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _logger.LogWarning("托盘窗口创建失败，错误码 {Code}", Marshal.GetLastWin32Error());
                return;
            }
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            AddIcon();
            _ready.Set();

            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
            RemoveIcon();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "托盘图标运行失败");
        }
        finally
        {
            _ready.Set();
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_TRAY)
        {
            var evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt == WM_LBUTTONDBLCLK) OpenUi();
            else if (evt == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        if (message == WM_CLOSE)
        {
            DestroyWindow(hWnd);
            return IntPtr.Zero;
        }
        // Explorer 重启后任务栏会清空图标，收到广播后重新注册。
        if (_taskbarCreated != 0 && message == _taskbarCreated) AddIcon();
        return DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private void AddIcon()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_icon == IntPtr.Zero)
        {
            var file = Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico");
            if (File.Exists(file))
            {
                _icon = LoadImageW(IntPtr.Zero, file, ImageIcon, 16, 16, LrLoadFromFile);
                _ownsIcon = _icon != IntPtr.Zero;
            }
            if (_icon == IntPtr.Zero) _icon = LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication));
        }
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
            szTip = _tip
        };
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
            _logger.LogWarning("托盘图标注册失败，错误码 {Code}", Marshal.GetLastWin32Error());
    }

    private void RemoveIcon()
    {
        if (_hwnd == IntPtr.Zero) return;
        var data = new NotifyIconData { cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = _hwnd, uID = 1 };
        try { Shell_NotifyIconW(NIM_DELETE, ref data); } catch (Exception) { }
        if (_ownsIcon) { try { DestroyIcon(_icon); } catch (Exception) { } }
        _icon = IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_OPEN), "打开界面");
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_LOGS), "打开日志目录");
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_EXIT), "退出");
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            switch (command)
            {
                case ID_OPEN: OpenUi(); break;
                case ID_LOGS: OpenLogs(); break;
                case ID_EXIT: Task.Run(_exit); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    private void OpenUi()
    {
        try { Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true }); }
        catch (Exception ex) { _logger.LogWarning(ex, "打开界面失败：{Url}", _url); }
    }

    private void OpenLogs()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logRoot}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _logger.LogWarning(ex, "打开日志目录失败：{Path}", _logRoot); }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out Msg message, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref Msg message);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr item, string? text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}