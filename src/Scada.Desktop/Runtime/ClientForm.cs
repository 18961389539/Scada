using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Scada.Desktop.Api;
using Scada.Desktop.App;
using Scada.Desktop.Configuration;

namespace Scada.Desktop.Runtime;

/// <summary>
/// 实际显示组态画面的窗口。窗口本身只负责登录、注入会话、全屏和崩溃恢复，
/// 画面导航、权限和实时数据全部交给现有 Web 前端。
/// </summary>
internal sealed class ClientForm : Form
{
    private const int WmHotKey = 0x0312;
    private const int ExitHotKeyId = 0x5C01;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;

    private readonly DesktopSettings _settings;
    private readonly DesktopPaths _paths;
    private readonly int _parentProcessId;
    private readonly string _bootId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ServerApiClient _api;
    private readonly Panel _overlay = new();
    private readonly Label _overlayTitle = new();
    private readonly Label _overlayDetail = new();

    private WebView2? _webView;
    private CoreWebView2? _core;
    private SessionData? _session;
    private System.Windows.Forms.Timer? _parentTimer;
    private System.Windows.Forms.Timer? _keepAwakeTimer;
    private System.Windows.Forms.Timer? _reloadTimer;
    private DateTime _lastLoginAttemptUtc = DateTime.MinValue;
    private bool _allowClose;
    private bool _shuttingDown;
    private bool _reloginInProgress;
    private bool _webViewRecovered;

    public ClientForm(DesktopSettings settings, DesktopPaths paths, int parentProcessId)
    {
        _settings = settings;
        _paths = paths;
        _parentProcessId = parentProcessId;
        _api = new ServerApiClient(settings.ServerUrl);

        Text = "SCADA 桌面客户端";
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(13, 22, 34);
        BuildOverlay();
        // 登录与网络重试阶段还没有 WebView2，覆盖层必须先挂在窗体上，
        // 否则无人值守终端在服务器未就绪时只会看到一片黑屏。
        Controls.Add(_overlay);
        ApplyMonitorBounds();
        try
        {
            using var stream = typeof(ClientForm).Assembly.GetManifestResourceStream("Scada.Desktop.app.ico");
            if (stream is not null) Icon = new Icon(stream);
        }
        catch
        {
            // 图标缺失不影响运行。
        }
    }

    public int ResultCode { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_parentProcessId > 0) StartParentWatchdog();
        if (_settings.KeepAwake) StartKeepAwake();
        _ = InitializeAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!RegisterHotKey(Handle, ExitHotKeyId, ModControl | ModAlt | ModShift, (uint)Keys.Q))
        {
            DesktopLogger.Warn("注册退出快捷键 Ctrl+Alt+Shift+Q 失败，请使用托盘退出");
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, ExitHotKeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey && m.WParam.ToInt32() == ExitHotKeyId)
        {
            ConfirmExit();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            // 无人值守终端不允许 Alt+F4 误退出；关机、注销和任务管理器仍可关闭。
            e.Cancel = true;
            return;
        }
        _shuttingDown = true;
        _lifetime.Cancel();
        _parentTimer?.Stop();
        _parentTimer?.Dispose();
        _keepAwakeTimer?.Stop();
        _keepAwakeTimer?.Dispose();
        _reloadTimer?.Stop();
        _reloadTimer?.Dispose();
        if (_settings.KeepAwake) SleepPreventer.Release();
        try { _webView?.Dispose(); } catch { /* 关闭阶段的异常不阻塞退出 */ }
        _api.Dispose();
        base.OnFormClosing(e);
    }

    private void ApplyMonitorBounds()
    {
        var screen = Screen.AllScreens.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(_settings.Display.MonitorDeviceName)
            && string.Equals(item.DeviceName, _settings.Display.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? Screen.PrimaryScreen;
        if (screen is null) return;

        if (_settings.Display.FullScreen)
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = screen.Bounds;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            var width = Math.Min(1280, Math.Max(640, screen.WorkingArea.Width - 160));
            var height = Math.Min(800, Math.Max(480, screen.WorkingArea.Height - 160));
            Bounds = new Rectangle(
                screen.WorkingArea.X + (screen.WorkingArea.Width - width) / 2,
                screen.WorkingArea.Y + (screen.WorkingArea.Height - height) / 2,
                width,
                height);
        }
    }

    private void BuildOverlay()
    {
        _overlay.Dock = DockStyle.Fill;
        _overlay.BackColor = Color.FromArgb(13, 22, 34);

        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            WrapContents = false,
            Margin = Padding.Empty
        };
        _overlayTitle.AutoSize = true;
        _overlayTitle.Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold);
        _overlayTitle.ForeColor = Color.FromArgb(220, 230, 240);
        _overlayTitle.Text = "正在启动…";

        _overlayDetail.AutoSize = true;
        _overlayDetail.MaximumSize = new Size(720, 0);
        _overlayDetail.Font = new Font("Microsoft YaHei UI", 10F);
        _overlayDetail.ForeColor = Color.FromArgb(150, 170, 190);
        _overlayDetail.Margin = new Padding(0, 12, 0, 0);

        stack.Controls.Add(_overlayTitle);
        stack.Controls.Add(_overlayDetail);
        center.Controls.Add(stack, 0, 1);
        _overlay.Controls.Add(center);
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (!WebViewRuntimeCheck.TryGetVersion(out var webViewVersion))
            {
                ShowFatal("本机未安装 Microsoft Edge WebView2 Runtime，无法显示组态画面。\n请安装后重试：" + WebViewRuntimeCheck.DownloadUrl);
                RequestExit(ExitCodes.WebViewMissing);
                return;
            }
            DesktopLogger.Info($"WebView2 Runtime：{webViewVersion}");

            SetOverlay("正在连接服务器…", _settings.ServerUrl);
            var (session, fatal) = await AcquireSessionAsync();
            if (fatal)
            {
                RequestExit(ExitCodes.Configuration);
                return;
            }
            _session = session;

            SetOverlay("正在打开画面…", string.Empty);
            await CreateWebViewAsync();
            if (_shuttingDown || _core is null) return;
            _core.Navigate(TargetUrl.Build(_settings));
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭导致的取消，不需要提示。
        }
        catch (Exception exception)
        {
            if (_shuttingDown) return;
            DesktopLogger.Error("客户端初始化失败", exception);
            ShowFatal("客户端启动失败：" + exception.Message);
            RequestExit(ExitCodes.Crash);
        }
    }

    private async Task<(SessionData? Session, bool Fatal)> AcquireSessionAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.PasswordCipher)) return (null, false);
        var password = PasswordProtector.Unprotect(_settings.PasswordCipher);
        if (password is null)
        {
            ShowFatal("无法解密已保存的密码。请打开设置重新输入账号密码。");
            return (null, true);
        }

        var attempts = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            attempts++;
            var result = await _api.LoginAsync(_settings.UserName ?? string.Empty, password, _lifetime.Token);
            switch (result.Status)
            {
                case LoginStatus.Success:
                    DesktopLogger.Info($"自动登录成功：{_settings.UserName}");
                    return (result.Session, false);
                case LoginStatus.MustChangePassword:
                    ShowFatal(result.Message);
                    return (null, true);
                case LoginStatus.InvalidCredentials:
                    ShowFatal(result.Message + "\n请在托盘菜单中打开设置，更新账号或密码。");
                    return (null, true);
                case LoginStatus.RateLimited:
                    var wait = result.RetryAfter is { } retry && retry > TimeSpan.Zero ? retry : TimeSpan.FromMinutes(1);
                    SetOverlay("登录暂时受限", $"{result.Message}\n{Math.Max(1, (int)wait.TotalSeconds)} 秒内不会重复尝试，请稍后在托盘中重新启动。");
                    await Task.Delay(wait, _lifetime.Token);
                    return (null, true);
                case LoginStatus.Network:
                case LoginStatus.Server:
                    SetOverlay("正在连接服务器…", $"{result.Message}\n第 {attempts} 次重试，15 秒后继续。");
                    await Task.Delay(TimeSpan.FromSeconds(15), _lifetime.Token);
                    break;
                default:
                    ShowFatal(result.Message);
                    return (null, true);
            }
        }
        return (null, true);
    }

    private async Task CreateWebViewAsync()
    {
        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(13, 22, 34)
        };
        Controls.Add(_webView);
        _overlay.BringToFront();

        var environment = await CoreWebView2Environment.CreateAsync(null, _paths.WebViewDirectory);
        await _webView.EnsureCoreWebView2Async(environment);
        _core = _webView.CoreWebView2;

        var browserSettings = _core.Settings;
        browserSettings.AreDefaultContextMenusEnabled = false;
        browserSettings.AreDevToolsEnabled = false;
        browserSettings.AreBrowserAcceleratorKeysEnabled = false;
        browserSettings.IsStatusBarEnabled = false;
        browserSettings.IsZoomControlEnabled = false;
        browserSettings.IsBuiltInErrorPageEnabled = false;
        browserSettings.IsPasswordAutosaveEnabled = false;
        browserSettings.IsGeneralAutofillEnabled = false;
        browserSettings.IsWebMessageEnabled = false;
        browserSettings.AreHostObjectsAllowed = false;

        await _core.AddScriptToExecuteOnDocumentCreatedAsync(
            InjectionScript.Build(_settings, _session, _bootId));

        _core.NavigationStarting += OnNavigationStarting;
        _core.NavigationCompleted += OnNavigationCompleted;
        _core.ProcessFailed += OnProcessFailed;
        _core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            DesktopLogger.Warn($"桌面客户端暂不支持弹出新窗口：{args.Uri}");
        };
        _core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            DesktopLogger.Warn("桌面客户端暂不支持下载文件");
        };
        _core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        _core.ServerCertificateErrorDetected += (_, args) =>
            DesktopLogger.Error($"服务器证书不受信任：{args.RequestUri}");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_shuttingDown) return;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (!TargetUrl.IsLoginPage(uri)) return;
        if (string.IsNullOrWhiteSpace(_settings.PasswordCipher)) return;
        if (_reloginInProgress) return;

        e.Cancel = true;
        _ = EnsureReLoggedInAsync();
    }

    private async Task EnsureReLoggedInAsync()
    {
        if (_reloginInProgress || _shuttingDown) return;
        _reloginInProgress = true;
        try
        {
            var password = PasswordProtector.Unprotect(_settings.PasswordCipher);
            if (password is null)
            {
                ShowFatal("无法解密已保存的密码，请在托盘中打开设置重新输入。");
                RequestExit(ExitCodes.Configuration);
                return;
            }

            var attempts = 0;
            while (!_shuttingDown && !_lifetime.IsCancellationRequested)
            {
                var sinceLast = DateTime.UtcNow - _lastLoginAttemptUtc;
                if (sinceLast < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10) - sinceLast, _lifetime.Token);
                }
                _lastLoginAttemptUtc = DateTime.UtcNow;
                attempts++;

                SetOverlay("会话已过期", "正在自动重新登录…");
                var result = await _api.LoginAsync(_settings.UserName ?? string.Empty, password, _lifetime.Token);
                switch (result.Status)
                {
                    case LoginStatus.Success:
                        _session = result.Session;
                        if (_core is not null)
                        {
                            await _core.ExecuteScriptAsync(
                                InjectionScript.Build(_settings, _session, _bootId, force: true));
                            _core.Navigate(TargetUrl.Build(_settings));
                        }
                        return;
                    case LoginStatus.Network:
                    case LoginStatus.Server:
                        SetOverlay("重新登录失败", $"{result.Message}\n10 秒后重试（第 {attempts} 次）。");
                        await Task.Delay(TimeSpan.FromSeconds(10), _lifetime.Token);
                        break;
                    default:
                        ShowFatal(result.Message + "\n请在托盘菜单中打开设置，更新账号或密码。");
                        RequestExit(ExitCodes.Configuration);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭。
        }
        catch (Exception exception)
        {
            if (_shuttingDown) return;
            DesktopLogger.Error("自动重新登录失败", exception);
            RequestExit(ExitCodes.Crash);
        }
        finally
        {
            _reloginInProgress = false;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_shuttingDown) return;
        if (e.IsSuccess)
        {
            _webViewRecovered = false;
            _ = HideOverlaySoonAsync();
            return;
        }
        if (_reloginInProgress) return;
        SetOverlay("无法打开画面", DescribeWebError(e.WebErrorStatus) + "\n10 秒后重试…");
        ScheduleReload();
    }

    private async Task HideOverlaySoonAsync()
    {
        try
        {
            await Task.Delay(400, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_shuttingDown || IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => _overlay.Visible = false));
            return;
        }
        _overlay.Visible = false;
        _webView?.Focus();
    }

    private void ScheduleReload()
    {
        if (_reloadTimer is not null) return;
        _reloadTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _reloadTimer.Tick += (_, _) =>
        {
            _reloadTimer?.Stop();
            _reloadTimer?.Dispose();
            _reloadTimer = null;
            if (_shuttingDown || _core is null) return;
            try { _core.Reload(); }
            catch (Exception exception) { DesktopLogger.Error("重新加载画面失败", exception); }
        };
        _reloadTimer.Start();
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        DesktopLogger.Error($"WebView2 进程异常：{e.ProcessFailedKind}");
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited && !_webViewRecovered)
        {
            _webViewRecovered = true;
            try
            {
                _core?.Reload();
                return;
            }
            catch (Exception exception)
            {
                DesktopLogger.Error("WebView2 恢复失败", exception);
            }
        }
        RequestExit(ExitCodes.Crash);
    }

    private void StartParentWatchdog()
    {
        _parentTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _parentTimer.Tick += (_, _) =>
        {
            try
            {
                using var parent = Process.GetProcessById(_parentProcessId);
                if (parent.HasExited) RequestExit(ExitCodes.Success);
            }
            catch (ArgumentException)
            {
                RequestExit(ExitCodes.Success);
            }
            catch
            {
                // 查询失败不退出，下一轮再试。
            }
        };
        _parentTimer.Start();
    }

    private void StartKeepAwake()
    {
        SleepPreventer.KeepAlive();
        _keepAwakeTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _keepAwakeTimer.Tick += (_, _) => SleepPreventer.KeepAlive();
        _keepAwakeTimer.Start();
    }

    private void ConfirmExit()
    {
        var result = MessageBox.Show(
            this,
            "确定退出 SCADA 桌面客户端吗？",
            "SCADA 桌面客户端",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (result == DialogResult.Yes) RequestExit(ExitCodes.Success);
    }

    private void RequestExit(int code)
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        ResultCode = code;
        _allowClose = true;
        try { Close(); } catch { /* 关闭阶段忽略异常 */ }
    }

    private void SetOverlay(string title, string? detail)
    {
        if (_shuttingDown || IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => SetOverlay(title, detail))); } catch { }
            return;
        }
        _overlayTitle.Text = title;
        _overlayDetail.Text = detail ?? string.Empty;
        _overlay.Visible = true;
        _overlay.BringToFront();
    }

    private void ShowFatal(string message)
    {
        DesktopLogger.Error("客户端致命错误：" + message);
        try
        {
            MessageBox.Show(this, message, "SCADA 桌面客户端", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            // 窗口已销毁时无法弹窗，交给日志和退出码。
        }
    }

    private static string DescribeWebError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "找不到服务器，请检查地址和 DNS 设置",
        CoreWebView2WebErrorStatus.CannotConnect => "无法连接服务器，请确认服务端已启动",
        CoreWebView2WebErrorStatus.ServerUnreachable => "网络不可达，请检查本机网络连接",
        CoreWebView2WebErrorStatus.Timeout => "连接服务器超时",
        CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset
            or CoreWebView2WebErrorStatus.Disconnected => "与服务器的连接已断开",
        CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.CertificateIsInvalid
            or CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
            or CoreWebView2WebErrorStatus.CertificateRevoked => "服务器证书不受信任，请先安装内部 CA 证书",
        CoreWebView2WebErrorStatus.OperationCanceled => "页面加载已取消",
        _ => $"页面加载失败（{status}）"
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
