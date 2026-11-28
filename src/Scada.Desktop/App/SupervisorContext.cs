using System.Diagnostics;
using System.Runtime.InteropServices;
using Scada.Desktop.Configuration;
using Scada.Desktop.Runtime;
using Scada.Desktop.Ui;

namespace Scada.Desktop.App;

/// <summary>
/// 守护进程：托盘、设置入口、启动画面进程并在崩溃后按退避重启。
/// 多屏布局会按逻辑显示器拉起多个画面进程；任一窗口正常退出则全部退出。
/// 画面进程退出码 0 表示用户主动退出，守护进程随之退出。
/// </summary>
internal sealed class SupervisorContext : ApplicationContext
{
    private const int SwRestore = 9;

    private readonly DesktopPaths _paths;
    private readonly SettingsStore _store;
    private readonly RestartPolicy _restartPolicy = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<Process> _clients = [];
    private DesktopSettings _settings;

    private DateTime _clientStartedUtc;
    private DateTime? _pendingRestartAt;
    private bool _stoppingClient;
    private bool _restartAfterStop;
    private bool _settingsAfterStop;
    private bool _exiting;
    private bool _webViewWarningShown;
    private Icon? _trayIcon;

    public SupervisorContext(DesktopPaths paths, SettingsStore store, DesktopSettings settings)
    {
        _paths = paths;
        _store = store;
        _settings = settings;

        _menu = new ContextMenuStrip();
        _menu.Items.Add("打开画面", null, (_, _) => ShowClient());
        _menu.Items.Add("重新加载画面", null, (_, _) => ReloadClient());
        _menu.Items.Add("设置…", null, (_, _) => OpenSettingsFromTray());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("打开日志目录", null, (_, _) => OpenLogsDirectory());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => ConfirmExit());

        _tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "SCADA 桌面客户端",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.DoubleClick += (_, _) => ShowClient();

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        StartClient();
    }

    public int ExitCode { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _trayIcon?.Dispose();
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { /* 退出阶段忽略 */ }
            }
            _clients.Clear();
        }
        base.Dispose(disposing);
    }

    private bool HasRunningClient()
    {
        foreach (var client in _clients)
        {
            try { if (!client.HasExited) return true; } catch { /* 进程已回收 */ }
        }
        return false;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_exiting) return;

        Process? finished = null;
        var code = 0;
        foreach (var client in _clients.ToArray())
        {
            try
            {
                if (!client.HasExited) continue;
                finished = client;
                try { code = client.ExitCode; } catch { /* 进程已回收 */ }
                break;
            }
            catch
            {
                finished = client;
                break;
            }
        }
        if (finished is not null)
        {
            var runDuration = DateTime.UtcNow - _clientStartedUtc;
            _clients.Remove(finished);
            try { finished.Dispose(); } catch { /* 忽略 */ }
            HandleClientExit(code, runDuration);
            return;
        }

        if (_pendingRestartAt is { } at && DateTime.UtcNow >= at)
        {
            _pendingRestartAt = null;
            StartClient();
        }
    }

    private void HandleClientExit(int code, TimeSpan runDuration)
    {
        if (_stoppingClient)
        {
            if (HasRunningClient()) return;
            _stoppingClient = false;
            var openSettings = _settingsAfterStop;
            var restart = _restartAfterStop;
            _settingsAfterStop = false;
            _restartAfterStop = false;
            if (openSettings) ShowSettings(restartAfter: true, DesktopLaunchError.Read(_paths));
            else if (restart) StartClient();
            return;
        }

        if (code == ExitCodes.Success)
        {
            StopClient(restartAfter: false, settingsAfter: false);
            ExitApplication(ExitCodes.Success);
            return;
        }

        if (code == ExitCodes.Configuration)
        {
            StopClient(restartAfter: false, settingsAfter: false);
            var launchError = DesktopLaunchError.Read(_paths);
            var result = MessageBox.Show(
                DesktopLaunchError.SupervisorPrompt(launchError),
                "SCADA 桌面客户端",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (result == DialogResult.Yes) ShowSettings(restartAfter: true, launchError);
            else Balloon("自动登录失败，可在托盘中打开设置或重新启动画面。", ToolTipIcon.Warning);
            return;
        }

        if (code == ExitCodes.WebViewMissing)
        {
            StopClient(restartAfter: false, settingsAfter: false);
            ShowWebViewMissing();
            return;
        }

        var delay = _restartPolicy.RecordCrash(DateTime.UtcNow, runDuration);
        StopClient(restartAfter: false, settingsAfter: false);
        if (delay is null)
        {
            Balloon("画面进程多次异常退出，已暂停自动重启。可在托盘中重新启动或打开设置。", ToolTipIcon.Error);
            return;
        }
        _pendingRestartAt = DateTime.UtcNow + delay.Value;
        Balloon($"画面进程异常退出，{Math.Max(1, (int)delay.Value.TotalSeconds)} 秒后自动重启。", ToolTipIcon.Warning);
    }

    private void StartClient()
    {
        if (_exiting || HasRunningClient()) return;
        if (!WebViewRuntimeCheck.TryGetVersion(out _))
        {
            ShowWebViewMissing();
            return;
        }
        try
        {
            var plan = LayoutMonitorLaunch.Plan(_settings);
            var multi = plan.Count > 1;
            foreach (var target in plan)
            {
                _clients.Add(ClientLauncher.Start(
                    Environment.ProcessId,
                    multi ? target.Index : null,
                    target.DeviceName));
            }
            _clientStartedUtc = DateTime.UtcNow;
            _pendingRestartAt = null;
            DesktopLogger.Info(multi ? $"已启动 {plan.Count} 个画面进程（多屏布局）" : "已启动画面进程");
        }
        catch (Exception exception)
        {
            DesktopLogger.Error("启动画面进程失败", exception);
            Balloon("无法启动画面进程，请查看日志。", ToolTipIcon.Error);
        }
    }

    private void ShowClient()
    {
        var shown = false;
        foreach (var client in _clients)
        {
            try
            {
                if (client.HasExited) continue;
                client.Refresh();
                var handle = client.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                shown = true;
            }
            catch (Exception exception)
            {
                DesktopLogger.Warn("前置画面窗口失败：" + exception.Message);
            }
        }
        if (!shown) StartClient();
    }

    private void ReloadClient()
    {
        if (!HasRunningClient())
        {
            StartClient();
            return;
        }
        StopClient(restartAfter: true, settingsAfter: false);
    }

    private void OpenSettingsFromTray()
    {
        if (HasRunningClient())
        {
            StopClient(restartAfter: false, settingsAfter: true);
            return;
        }
        ShowSettings(restartAfter: true, DesktopLaunchError.Read(_paths));
    }

    private void StopClient(bool restartAfter, bool settingsAfter)
    {
        if (!HasRunningClient())
        {
            if (settingsAfter) ShowSettings(restartAfter);
            else if (restartAfter) StartClient();
            return;
        }
        _stoppingClient = true;
        _restartAfterStop = restartAfter;
        _settingsAfterStop = settingsAfter;
        foreach (var client in _clients.ToArray())
        {
            try
            {
                if (!client.HasExited) client.Kill(entireProcessTree: true);
            }
            catch (Exception exception)
            {
                DesktopLogger.Warn("结束画面进程失败：" + exception.Message);
            }
        }
    }

    private void ShowSettings(bool restartAfter, string? launchError = null)
    {
        var loaded = _store.Load(out var userError);
        var machine = new MachineSettingsStore(_paths.MachineSettingsFile).Load(out var machineError);
        var managedAutostart = InstallManifestStore.IsManagedAutostart(_paths);
        var resolved = DesktopConfigResolver.Resolve(loaded, machine, managedAutostart);
        var baseline = resolved?.Settings ?? _settings;
        var initialError = resolved?.Error ?? userError ?? machineError ?? launchError ?? DesktopLaunchError.Read(_paths);
        using var form = new SettingsForm(_paths, _store, baseline, initialError, machine);
        var result = form.ShowDialog();
        if (result == DialogResult.OK && form.SavedSettings is not null)
        {
            _settings = form.SavedSettings;
            ClientIdentity.Ensure(_settings, _store);
            _restartPolicy.Reset();
            DesktopLaunchError.Clear(_paths);
            DesktopLogger.Info("设置已保存");
        }
        if (DesktopSettingsCopy.ShouldStartClientAfterSettings(
                restartAfter, form.SavedSettings is not null, form.UnsavedServerPasswordChange))
            StartClient();
    }

    private void ConfirmExit()
    {
        var result = MessageBox.Show(
            "确定退出 SCADA 桌面客户端吗？",
            "SCADA 桌面客户端",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (result == DialogResult.Yes) ExitApplication(ExitCodes.Success);
    }

    private void ExitApplication(int code)
    {
        if (_exiting) return;
        _exiting = true;
        ExitCode = code;
        _timer.Stop();
        _tray.Visible = false;
        foreach (var client in _clients)
        {
            try { if (!client.HasExited) client.Kill(entireProcessTree: true); } catch { /* 退出阶段忽略 */ }
        }
        DesktopLogger.Info("守护进程退出");
        ExitThread();
    }

    private void ShowWebViewMissing()
    {
        if (_webViewWarningShown) return;
        _webViewWarningShown = true;
        var result = MessageBox.Show(
            "本机未安装 Microsoft Edge WebView2 Runtime，无法显示组态画面。\n\n是否打开官方下载页面？",
            "SCADA 桌面客户端",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result == DialogResult.Yes) OpenUrl(WebViewRuntimeCheck.DownloadUrl);
        Balloon("缺少 WebView2 Runtime，安装后可在托盘中重新启动画面。", ToolTipIcon.Error);
    }

    private void OpenLogsDirectory()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo { FileName = _paths.LogsDirectory, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            DesktopLogger.Warn("打开日志目录失败：" + exception.Message);
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* 打开浏览器失败时保留日志与提示 */ }
    }

    private void Balloon(string text, ToolTipIcon icon)
    {
        try
        {
            _tray.BalloonTipTitle = "SCADA 桌面客户端";
            _tray.BalloonTipText = text;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(4000);
        }
        catch
        {
            // 气泡提示失败不影响运行。
        }
    }

    private Icon LoadTrayIcon()
    {
        try
        {
            using var stream = typeof(SupervisorContext).Assembly.GetManifestResourceStream("Scada.Desktop.app.ico");
            if (stream is not null)
            {
                _trayIcon = new Icon(stream, SystemInformation.SmallIconSize);
                return _trayIcon;
            }
        }
        catch
        {
            // 退回系统图标。
        }
        return SystemIcons.Application;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
