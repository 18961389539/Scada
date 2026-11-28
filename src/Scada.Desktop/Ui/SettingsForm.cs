using System.Diagnostics;
using System.Linq;
using Scada.Desktop.Api;
using Scada.Desktop.Configuration;

namespace Scada.Desktop.Ui;

/// <summary>
/// 首启向导与日常设置共用的窗口。只做“连接、登录、选目标、写配置”，
/// 真正的运行会话由客户端进程负责。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly DesktopPaths _paths;
    private readonly SettingsStore _store;
    private readonly string[] _lockedFields;
    private readonly bool _managedAutostart;
    private readonly string? _existingCipher;
    private readonly string? _existingClientId;
    private readonly string? _existingScope;
    private readonly string _originalServerUrl;
    private readonly string? _originalUserName;
    private readonly string _originalKind;
    private readonly int? _originalScreenId;
    private readonly int? _originalLayoutId;
    private readonly int? _originalPlaylistId;
    private readonly int? _originalWorkstationId;

    private readonly TextBox _serverUrl = new();
    private readonly Button _connectButton = new() { Text = "测试连接", AutoSize = true };
    private readonly Label _siteInfo = new() { AutoSize = true, ForeColor = Color.FromArgb(80, 100, 120) };
    private readonly TextBox _userName = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _savePassword = new() { Text = "保存密码（Windows DPAPI 加密，仅当前用户可解密）", AutoSize = true, Checked = true };
    private readonly Button _loginButton = new() { Text = "登录并加载列表", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(620, 0) };
    private readonly RadioButton _kindScreen = new() { Text = "单个组态画面", AutoSize = true, Checked = true };
    private readonly RadioButton _kindLayout = new() { Text = "运行布局", AutoSize = true };
    private readonly RadioButton _kindPlaylist = new() { Text = "Kiosk 播放列表", AutoSize = true };
    private readonly RadioButton _kindWorkstation = new() { Text = "按工作站自动加载", AutoSize = true };
    private readonly RadioButton _kindWorkspace = new() { Text = "多页面工作台", AutoSize = true };
    private readonly ComboBox _screenCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly ComboBox _layoutCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly ComboBox _playlistCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly Panel _layoutMonitorPanel = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly ComboBox _workstationCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly CheckBox _monitorOnly = new() { Text = "默认以监视模式打开（播放列表若本身为监视模式，服务端会在播放期间禁止下发）", AutoSize = true, Checked = true };
    private readonly CheckBox _fullScreen = new() { Text = "全屏运行", AutoSize = true, Checked = true };
    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly CheckBox _autoStart = new() { Text = "Windows 登录后自动启动", AutoSize = true };
    private readonly CheckBox _keepAwake = new() { Text = "阻止系统休眠和关闭显示器", AutoSize = true, Checked = true };
    private readonly CheckBox _allowDownloads = new() { Text = "允许下载报表/导出文件", AutoSize = true };
    private readonly TextBox _downloadDirectory = new();
    private readonly Button _browseDirectory = new() { Text = "浏览…", AutoSize = true };
    private readonly Button _saveButton = new() { Text = "保存并启动", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "取消", AutoSize = true };
    private readonly Button _logsButton = new() { Text = "打开日志目录", AutoSize = true };
    private readonly Label _passwordHint = new() { AutoSize = true, ForeColor = Color.FromArgb(110, 125, 140) };
    private readonly Label _notice = new()
    {
        AutoSize = true,
        MaximumSize = new Size(660, 0),
        ForeColor = Color.FromArgb(80, 100, 120)
    };
    private readonly Label _demoNotice = new()
    {
        AutoSize = true,
        MaximumSize = new Size(660, 0),
        ForeColor = Color.FromArgb(80, 100, 120),
        Visible = false
    };
    private readonly Label _accountHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = Color.FromArgb(110, 125, 140)
    };
    private readonly Label _loginStatus = new() { AutoSize = true, MaximumSize = new Size(620, 0) };
    private readonly TextBox _currentPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _newPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _confirmPassword = new() { UseSystemPasswordChar = true };
    private readonly Button _changePasswordButton = new() { Text = "修改密码并继续", AutoSize = true };
    private readonly Button _openBrowserButton = new() { Text = "打开浏览器登录页", AutoSize = true };
    private readonly Button _browserDoneButton = new() { Text = DesktopSettingsCopy.BrowserDoneButton, AutoSize = true };
    private readonly Label _browserHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = Color.FromArgb(110, 125, 140)
    };
    private readonly Label _workspaceHint = new()
    {
        AutoSize = true,
        MaximumSize = new Size(620, 0),
        ForeColor = Color.FromArgb(80, 100, 120),
        Text = DesktopSettingsCopy.WorkspaceHintText(false),
        Visible = false
    };
    private readonly Label _screenLabel = NewLabel("组态画面");
    private readonly Label _layoutLabel = NewLabel("运行布局");
    private readonly Label _playlistLabel = NewLabel("播放列表");
    private readonly Label _workstationLabel = NewLabel("工作站");
    private readonly System.Windows.Forms.Timer _cooldownTimer = new() { Interval = 1000 };

    private ServerApiClient? _api;
    private string? _plainPassword;
    private bool _authenticated;
    private bool _targetListsLoaded;
    private bool _busy;
    private bool _closing;
    private DateTime _loginCooldownUntil = DateTime.MinValue;
    private string? _lastToken;
    private IReadOnlyList<ScreenSummary> _screens = [];
    private IReadOnlyList<LayoutSummary> _layouts = [];
    private IReadOnlyList<PlaylistSummary> _playlists = [];
    private IReadOnlyList<WorkstationSummary> _workstations = [];
    private string[]? _layoutMonitorDevices;
    private readonly string? _initialError;
    private bool _mustChangePending;
    private bool _savedCipherStale;
    private bool _passwordChanged;
    private bool _rejectSavedCipher;
    private bool _awaitingNewPassword;
    private bool _openedBrowser;
    private bool _demoMode;
    private bool _serverPasswordUpdated;
    private SessionData? _pendingSession;

    public SettingsForm(DesktopPaths paths, SettingsStore store, DesktopSettings? existing, string? initialError, MachineSettings? machine = null)
    {
        _paths = paths;
        _store = store;
        _lockedFields = existing?.LockedFields ?? [];
        _managedAutostart = existing?.ManagedAutostart ?? false;
        _existingCipher = existing?.PasswordCipher;
        _existingClientId = existing?.ClientId;
        _existingScope = existing?.ConfigScope;
        _originalServerUrl = existing?.ServerUrl ?? "http://localhost:5080";
        _originalUserName = existing?.UserName;
        _originalKind = existing?.Target.Kind ?? "screen";
        _originalScreenId = existing?.Target.ScreenId;
        _originalLayoutId = existing?.Target.LayoutId;
        _originalPlaylistId = existing?.Target.PlaylistId;
        _originalWorkstationId = existing?.Target.WorkstationId;
        _layoutMonitorDevices = existing?.Target.LayoutMonitorDeviceNames;

        _initialError = initialError;
        _mustChangePending = false;
        _savedCipherStale = false;
        _rejectSavedCipher = DesktopLaunchError.IsInvalidCredentials(initialError);

        Text = existing is null ? "SCADA 桌面客户端 - 首次配置" : "SCADA 桌面客户端 - 设置";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = !DesktopSettingsCopy.ShouldOpenWithoutTopMost(initialError);
        AutoScroll = true;
        ClientSize = new Size(760, 930);

        BuildUi(existing, initialError);
        PopulateMonitors(existing?.Display.MonitorDeviceName);
        ApplyExistingTarget(existing);
        UpdatePasswordHint();
        _status.Text = string.Empty;
        if (_rejectSavedCipher)
            SetLoginStatus(DesktopSettingsCopy.CredentialsRejectedHint, true);

        _connectButton.Click += ConnectButton_Click;
        _loginButton.Click += LoginButton_Click;
        _changePasswordButton.Click += ChangePasswordButton_Click;
        _openBrowserButton.Click += OpenBrowserButton_Click;
        _browserDoneButton.Click += BrowserDoneButton_Click;
        _saveButton.Click += SaveButton_Click;
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _logsButton.Click += (_, _) => OpenLogsDirectory();
        _browseDirectory.Click += (_, _) => BrowseDownloadDirectory();
        _kindScreen.CheckedChanged += (_, _) => UpdateTargetControls();
        _kindLayout.CheckedChanged += (_, _) => UpdateTargetControls();
        _kindPlaylist.CheckedChanged += (_, _) => UpdateTargetControls();
        _kindWorkstation.CheckedChanged += (_, _) => UpdateTargetControls();
        _layoutCombo.SelectedIndexChanged += (_, _) => { if (CurrentKind() == "layout") _ = RefreshLayoutMonitorsAsync(); };
        _monitorOnly.CheckedChanged += (_, _) => UpdateTargetControls();
        _kindWorkspace.CheckedChanged += (_, _) => UpdateTargetControls();
        _serverUrl.TextChanged += (_, _) => MarkConnectionDirty();
        _userName.TextChanged += (_, _) => MarkConnectionDirty();
        _password.TextChanged += (_, _) => MarkConnectionDirty();
        _cooldownTimer.Tick += CooldownTimer_Tick;

        UpdateTargetControls();
        UpdateButtons();
        ApplyLocks();
        UpdatePasswordChangeUi();
        _ = ProbeSiteInfoAsync();
    }

    public DesktopSettings? SavedSettings { get; private set; }

    public bool UnsavedServerPasswordChange => _serverPasswordUpdated && SavedSettings is null;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (UnsavedServerPasswordChange && !_closing)
        {
            var answer = MessageBox.Show(
                this,
                DesktopSettingsCopy.UnsavedPasswordCloseMessage,
                "SCADA 桌面客户端",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (answer == DialogResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (answer == DialogResult.Yes && !TrySave())
            {
                e.Cancel = true;
                return;
            }
        }
        _closing = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _cooldownTimer.Dispose();
            _api?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildUi(DesktopSettings? existing, string? initialError)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16),
            AutoSize = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 7; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildNotice(initialError), 0, 0);
        root.Controls.Add(BuildConnectionGroup(), 0, 1);
        root.Controls.Add(BuildAccountGroup(), 0, 2);
        root.Controls.Add(BuildTargetGroup(), 0, 3);
        root.Controls.Add(BuildOptionsGroup(), 0, 4);
        root.Controls.Add(BuildDownloadGroup(), 0, 5);
        root.Controls.Add(BuildButtons(), 0, 6);
        Controls.Add(root);
    }

    private Control BuildNotice(string? initialError)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8)
        };
        _notice.Text = DesktopSettingsCopy.Notice(null, false);
        panel.Controls.Add(_notice);
        panel.Controls.Add(_demoNotice);
        if (_lockedFields.Length > 0 || _managedAutostart)
        {
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(660, 0),
                ForeColor = Color.FromArgb(170, 110, 20),
                Text = "部分选项由管理员统一配置，已锁定，不能在此修改。"
            });
        }
        if (!string.IsNullOrWhiteSpace(initialError))
        {
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(660, 0),
                ForeColor = Color.FromArgb(180, 60, 60),
                Text = initialError
            });
        }
        _status.AutoSize = true;
        _status.MaximumSize = new Size(660, 0);
        _status.Margin = new Padding(0, 8, 0, 0);
        panel.Controls.Add(_status);
        return panel;
    }

    private Control BuildConnectionGroup()
    {
        var group = NewGroup("服务器");
        var table = NewGrid(3);
        table.Controls.Add(NewLabel("服务器地址"), 0, 0);
        table.Controls.Add(_serverUrl, 1, 0);
        _serverUrl.Dock = DockStyle.Fill;
        _serverUrl.PlaceholderText = "http://192.168.1.10:5080";
        table.Controls.Add(_connectButton, 2, 0);
        table.Controls.Add(_siteInfo, 1, 1);
        table.SetColumnSpan(_siteInfo, 2);
        group.Controls.Add(table);
        return group;
    }

    private Control BuildAccountGroup()
    {
        var group = NewGroup("登录账号");
        var table = NewGrid(3);
        table.Controls.Add(_accountHint, 0, 0);
        table.SetColumnSpan(_accountHint, 3);
        table.Controls.Add(NewLabel("用户名"), 0, 1);
        table.Controls.Add(_userName, 1, 1);
        _userName.Dock = DockStyle.Fill;
        table.Controls.Add(NewLabel("密码"), 0, 2);
        table.Controls.Add(_password, 1, 2);
        _password.Dock = DockStyle.Fill;
        table.Controls.Add(_loginButton, 2, 2);
        table.Controls.Add(_savePassword, 1, 3);
        table.SetColumnSpan(_savePassword, 2);
        table.Controls.Add(_passwordHint, 1, 4);
        table.SetColumnSpan(_passwordHint, 2);
        table.Controls.Add(_loginStatus, 1, 5);
        table.SetColumnSpan(_loginStatus, 2);
        var changePanel = BuildPasswordChangePanel();
        table.Controls.Add(changePanel, 0, 6);
        table.SetColumnSpan(changePanel, 3);
        group.Controls.Add(table);
        return group;
    }

    private Control BuildPasswordChangePanel()
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Margin = new Padding(0, 8, 0, 0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        table.Controls.Add(NewLabel("当前密码"), 0, 0);
        table.Controls.Add(_currentPassword, 1, 0);
        _currentPassword.Dock = DockStyle.Fill;
        table.Controls.Add(NewLabel("新密码"), 0, 1);
        table.Controls.Add(_newPassword, 1, 1);
        _newPassword.Dock = DockStyle.Fill;
        table.Controls.Add(NewLabel("确认新密码"), 0, 2);
        table.Controls.Add(_confirmPassword, 1, 2);
        _confirmPassword.Dock = DockStyle.Fill;
        table.Controls.Add(_changePasswordButton, 2, 2);
        table.Controls.Add(_openBrowserButton, 1, 3);
        table.Controls.Add(_browserDoneButton, 1, 4);
        table.SetColumnSpan(_browserDoneButton, 2);
        table.Controls.Add(_browserHint, 1, 5);
        table.SetColumnSpan(_browserHint, 2);
        table.Name = "passwordChangePanel";
        return table;
    }

    private Control BuildTargetGroup()
    {
        var group = NewGroup("显示内容");
        var table = NewGrid(3);
        var kinds = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        kinds.Controls.Add(_kindScreen);
        kinds.Controls.Add(_kindLayout);
        kinds.Controls.Add(_kindPlaylist);
        kinds.Controls.Add(_kindWorkstation);
        kinds.Controls.Add(_kindWorkspace);
        table.Controls.Add(kinds, 0, 0);
        table.SetColumnSpan(kinds, 3);

        table.Controls.Add(_workspaceHint, 0, 1);
        table.SetColumnSpan(_workspaceHint, 3);
        table.Controls.Add(_screenLabel, 0, 2);
        table.Controls.Add(_screenCombo, 1, 2);
        table.Controls.Add(_layoutLabel, 0, 3);
        table.Controls.Add(_layoutCombo, 1, 3);
        table.Controls.Add(_playlistLabel, 0, 4);
        table.Controls.Add(_playlistCombo, 1, 4);
        table.Controls.Add(_workstationLabel, 0, 5);
        table.Controls.Add(_workstationCombo, 1, 5);
        table.Controls.Add(_monitorOnly, 1, 6);
        table.SetColumnSpan(_monitorOnly, 2);
        table.Controls.Add(_layoutMonitorPanel, 1, 7);
        table.SetColumnSpan(_layoutMonitorPanel, 2);
        group.Controls.Add(table);
        return group;
    }

    private Control BuildOptionsGroup()
    {
        var group = NewGroup("显示与启动");
        var table = NewGrid(3);
        table.Controls.Add(_fullScreen, 0, 0);
        table.SetColumnSpan(_fullScreen, 2);
        table.Controls.Add(NewLabel("显示器"), 0, 1);
        table.Controls.Add(_monitorCombo, 1, 1);
        table.Controls.Add(_autoStart, 0, 2);
        table.SetColumnSpan(_autoStart, 2);
        table.Controls.Add(_keepAwake, 0, 3);
        table.SetColumnSpan(_keepAwake, 2);
        group.Controls.Add(table);
        return group;
    }

    private Control BuildDownloadGroup()
    {
        var group = NewGroup("导出与下载");
        var table = NewGrid(3);
        table.Controls.Add(_allowDownloads, 0, 0);
        table.SetColumnSpan(_allowDownloads, 3);
        table.Controls.Add(NewLabel("下载目录"), 0, 1);
        table.Controls.Add(_downloadDirectory, 1, 1);
        _downloadDirectory.Dock = DockStyle.Fill;
        _downloadDirectory.PlaceholderText = "留空使用用户下载目录";
        table.Controls.Add(_browseDirectory, 2, 1);
        group.Controls.Add(table);
        return group;
    }

    private Control BuildButtons()
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 0)
        };
        panel.Controls.Add(_saveButton);
        panel.Controls.Add(_cancelButton);
        panel.Controls.Add(_logsButton);
        return panel;
    }

    private static GroupBox NewGroup(string title) => new()
    {
        Text = title,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Fill,
        Padding = new Padding(12),
        Margin = new Padding(0, 0, 0, 10)
    };

    private static TableLayoutPanel NewGrid(int columns)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = columns,
            RowCount = 8
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        if (columns > 2) table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        return table;
    }

    private static Label NewLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6, 8, 6)
    };

    private void PopulateMonitors(string? selectedDeviceName)
    {
        var options = Screen.AllScreens
            .Select((screen, index) => new MonitorOption(
                screen.DeviceName,
                $"{(screen.Primary ? "主显示器" : $"显示器 {index + 1}")}  {screen.Bounds.Width}×{screen.Bounds.Height}"))
            .ToList();
        _monitorCombo.Items.Clear();
        foreach (var option in options) _monitorCombo.Items.Add(option);
        var selected = options.FirstOrDefault(option =>
            string.Equals(option.DeviceName, selectedDeviceName, StringComparison.OrdinalIgnoreCase));
        _monitorCombo.SelectedItem = selected ?? options.FirstOrDefault();
    }

    private void ApplyExistingTarget(DesktopSettings? existing)
    {
        if (existing is null)
        {
            _kindScreen.Checked = true;
            _fullScreen.Checked = true;
            _keepAwake.Checked = true;
            _savePassword.Checked = true;
            return;
        }
        switch (existing.Target.Kind)
        {
            case "layout": _kindLayout.Checked = true; break;
            case "playlist": _kindPlaylist.Checked = true; break;
            case "workstation": _kindWorkstation.Checked = true; break;
            case "workspace": _kindWorkspace.Checked = true; break;
            default: _kindScreen.Checked = true; break;
        }
        _monitorOnly.Checked = existing.Target.MonitorOnly;
        _fullScreen.Checked = existing.Display.FullScreen;
        _keepAwake.Checked = existing.KeepAwake;
        _autoStart.Checked = existing.AutoStart;
        _savePassword.Checked = existing.SavePassword;
        _allowDownloads.Checked = existing.AllowDownloads;
        _downloadDirectory.Text = existing.DownloadDirectory ?? string.Empty;
        _serverUrl.Text = existing.ServerUrl;
        _userName.Text = existing.UserName ?? string.Empty;

        if (existing.Target.ScreenId is int screenId) _screenCombo.Items.Add(new ScreenOption(screenId, $"当前画面 #{screenId}（登录后刷新名称）"));
        if (existing.Target.LayoutId is int layoutId) _layoutCombo.Items.Add(new LayoutOption(layoutId, $"当前布局 #{layoutId}（登录后刷新名称）"));
        if (existing.Target.PlaylistId is int playlistId) _playlistCombo.Items.Add(new PlaylistOption(playlistId, $"当前播放列表 #{playlistId}（登录后刷新名称）"));
        if (existing.Target.WorkstationId is int stationId)
        {
            _workstationCombo.Items.Add(new WorkstationOption(stationId, existing.Target.WorkstationName ?? $"工作站 #{stationId}", $"当前工作站 #{stationId}（登录后刷新名称）"));
        }
        _screenCombo.SelectedIndex = _screenCombo.Items.Count > 0 ? 0 : -1;
        _layoutCombo.SelectedIndex = _layoutCombo.Items.Count > 0 ? 0 : -1;
        _playlistCombo.SelectedIndex = _playlistCombo.Items.Count > 0 ? 0 : -1;
        _workstationCombo.SelectedIndex = _workstationCombo.Items.Count > 0 ? 0 : -1;
    }

    private void UpdateTargetControls()
    {
        var kind = CurrentKind();
        var locked = IsLocked("target");
        var workspace = kind == "workspace";
        _workspaceHint.Text = DesktopSettingsCopy.WorkspaceHintText(_mustChangePending);
        _workspaceHint.Visible = workspace;
        _screenLabel.Visible = !workspace;
        _screenCombo.Visible = !workspace;
        _layoutLabel.Visible = !workspace;
        _layoutCombo.Visible = !workspace;
        _playlistLabel.Visible = !workspace;
        _playlistCombo.Visible = !workspace;
        _workstationLabel.Visible = !workspace;
        _workstationCombo.Visible = !workspace;
        _monitorOnly.Visible = !workspace;
        _screenCombo.Enabled = !locked && kind == "screen";
        _monitorOnly.Enabled = !locked && (kind == "screen" || kind == "playlist");
        _layoutCombo.Enabled = !locked && kind == "layout";
        _playlistCombo.Enabled = !locked && kind == "playlist";
        _workstationCombo.Enabled = !locked && (kind == "workstation" || kind == "screen" && _monitorOnly.Checked);
        _layoutMonitorPanel.Visible = kind == "layout";
        if (kind == "layout") _ = RefreshLayoutMonitorsAsync();
        UpdateAccountCopy();
        if (_targetListsLoaded) ApplyTargetListLoadStatus();
    }

    private bool IsLocked(string field) =>
        _lockedFields.Contains(field, StringComparer.OrdinalIgnoreCase);

    private async Task RefreshLayoutMonitorsAsync()
    {
        if (_api is null || CurrentKind() != "layout" || _layoutCombo.SelectedItem is not LayoutOption option)
        {
            RebuildLayoutMonitorRows(1, _layoutMonitorDevices);
            return;
        }
        try
        {
            var session = TryCurrentToken();
            if (session is null)
            {
                RebuildLayoutMonitorRows(_layoutMonitorDevices?.Length ?? 1, _layoutMonitorDevices);
                return;
            }
            var detail = await _api.GetLayoutAsync(option.Id, session);
            if (_closing || IsDisposed) return;
            var count = Math.Max(1, detail.Document?.Monitors?.Count ?? _layoutMonitorDevices?.Length ?? 1);
            RebuildLayoutMonitorRows(count, _layoutMonitorDevices);
        }
        catch
        {
            if (!_closing && !IsDisposed) RebuildLayoutMonitorRows(_layoutMonitorDevices?.Length ?? 1, _layoutMonitorDevices);
        }
    }

    private string? TryCurrentToken()
    {
        // 登录成功后的 token 存在 ServerApiClient 会话中；设置窗通过最近一次登录列表即可。
        return _lastToken;
    }

    private int LayoutMonitorCount() => Math.Max(1, _layoutMonitorPanel.Controls.OfType<ComboBox>().Count());

    private string[] CurrentLayoutMonitorDevices()
    {
        return _layoutMonitorPanel.Controls.OfType<ComboBox>()
            .Select(box => (box.SelectedItem as MonitorOption)?.DeviceName ?? "")
            .ToArray();
    }

    private void RebuildLayoutMonitorRows(int count, string[]? selected)
    {
        _layoutMonitorPanel.Controls.Clear();
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < count; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = $"逻辑显示器 {i + 1}", AutoSize = true, Anchor = AnchorStyles.Left }, 0, i);
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, Tag = i };
            combo.Items.Add(new MonitorOption("", "自动分配"));
            foreach (var screen in Screen.AllScreens)
            {
                var index = combo.Items.Count;
                combo.Items.Add(new MonitorOption(screen.DeviceName, $"{(screen.Primary ? "主显示器" : "显示器")} {screen.DeviceName}  {screen.Bounds.Width}×{screen.Bounds.Height}"));
            }
            var want = selected is not null && i < selected.Length ? selected[i] : null;
            var match = combo.Items.Cast<MonitorOption>().ToList().FindIndex(item => !string.IsNullOrWhiteSpace(want) && string.Equals(item.DeviceName, want, StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = match >= 0 ? match : 0;
            table.Controls.Add(combo, 1, i);
        }
        _layoutMonitorPanel.Controls.Add(table);
        _layoutMonitorDevices = CurrentLayoutMonitorDevices();
    }


    private void ApplyLocks()
    {
        if (_lockedFields.Length == 0 && !_managedAutostart) return;
        if (IsLocked("serverUrl")) _serverUrl.Enabled = false;
        if (IsLocked("userName")) _userName.Enabled = false;
        if (IsLocked("target"))
        {
            _kindScreen.Enabled = false;
            _kindLayout.Enabled = false;
            _kindPlaylist.Enabled = false;
            _kindWorkstation.Enabled = false;
            _kindWorkspace.Enabled = false;
        }
        if (IsLocked("display"))
        {
            _fullScreen.Enabled = false;
            _monitorCombo.Enabled = false;
        }
        if (IsLocked("autoStart") || _managedAutostart)
        {
            _autoStart.Enabled = false;
            if (_managedAutostart) _autoStart.Text = "Windows 登录后自动启动（由安装程序管理）";
        }
        if (IsLocked("allowDownloads")) _allowDownloads.Enabled = false;
        if (IsLocked("downloadDirectory"))
        {
            _downloadDirectory.Enabled = false;
            _browseDirectory.Enabled = false;
        }
    }

    private void BrowseDownloadDirectory()
    {
        if (IsLocked("downloadDirectory")) return;
        using var dialog = new FolderBrowserDialog { Description = "选择下载目录", ShowNewFolderButton = true };
        if (!string.IsNullOrWhiteSpace(_downloadDirectory.Text)) dialog.SelectedPath = _downloadDirectory.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _downloadDirectory.Text = dialog.SelectedPath;
    }

    private string CurrentKind() =>
        _kindLayout.Checked ? "layout" : _kindPlaylist.Checked ? "playlist" : _kindWorkstation.Checked ? "workstation" : _kindWorkspace.Checked ? "workspace" : "screen";

    private void MarkConnectionDirty()
    {
        if (!_authenticated) return;
        var urlChanged = !string.Equals(_serverUrl.Text.Trim(), _originalServerUrl, StringComparison.OrdinalIgnoreCase);
        var userChanged = !string.Equals(_userName.Text.Trim(), _originalUserName ?? string.Empty, StringComparison.Ordinal);
        var passwordChanged = _password.Text.Length > 0;
        if (!urlChanged && !userChanged && !passwordChanged) return;
        _authenticated = false;
        SetStatus("连接信息已修改，请重新登录并加载列表。", true);
    }

    private async void ConnectButton_Click(object? sender, EventArgs e)
    {
        if (!SettingsValidator.TryNormalizeServerUrl(_serverUrl.Text, out var url, out var error))
        {
            SetStatus(error, true);
            return;
        }
        _serverUrl.Text = url;
        SetBusy(true);
        try
        {
            using var probe = new ServerApiClient(url);
            var info = await probe.GetSiteInfoAsync();
            if (_closing || IsDisposed) return;
            ApplySiteInfo(info);
            SetStatus("服务器连接正常，请继续登录。", false);
        }
        catch (ApiException exception)
        {
            _siteInfo.Text = "连接失败";
            SetStatus(exception.Message, true);
        }
        catch (Exception exception)
        {
            _siteInfo.Text = "连接失败";
            SetStatus("连接失败：" + exception.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LoginButton_Click(object? sender, EventArgs e)
    {
        if (!SettingsValidator.TryNormalizeServerUrl(_serverUrl.Text, out var url, out var error))
        {
            SetStatus(error, true);
            return;
        }
        _serverUrl.Text = url;
        var userName = _userName.Text.Trim();
        if (userName.Length == 0)
        {
            SetStatus("请填写用户名", true);
            return;
        }
        var password = _password.Text;
        if (password.Length == 0)
        {
            if (!DesktopSettingsCopy.CanReuseSavedCipher(_savedCipherStale, _rejectSavedCipher || _awaitingNewPassword))
            {
                SetStatus(
                    _awaitingNewPassword ? DesktopSettingsCopy.AwaitingNewPasswordHint
                    : _savedCipherStale || _passwordChanged ? DesktopSettingsCopy.SavedCipherStaleHint
                    : DesktopSettingsCopy.CredentialsRejectedHint,
                    true);
                return;
            }
            if (!string.Equals(userName, _originalUserName ?? string.Empty, StringComparison.Ordinal))
            {
                SetStatus("用户名已修改，请重新输入密码", true);
                return;
            }
            password = PasswordProtector.Unprotect(_existingCipher) ?? string.Empty;
            if (password.Length == 0)
            {
                SetStatus("请输入密码", true);
                return;
            }
        }

        SetBusy(true);
        try
        {
            _api?.Dispose();
            _api = new ServerApiClient(url);
            var result = await _api.LoginAsync(userName, password);
            if (_closing || IsDisposed) return;
            switch (result.Status)
            {
                case LoginStatus.Success:
                    _plainPassword = password;
                    _authenticated = true;
                    _mustChangePending = false;
                    _pendingSession = result.Session;
                    if (_awaitingNewPassword || _openedBrowser && _rejectSavedCipher)
                    {
                        _passwordChanged = true;
                        _savedCipherStale = true;
                        _serverPasswordUpdated = true;
                    }
                    else if (_rejectSavedCipher && _password.Text.Length > 0)
                    {
                        _passwordChanged = true;
                        _savedCipherStale = true;
                    }
                    _awaitingNewPassword = false;
                    SetStatus($"登录成功：{userName}", false);
                    SetLoginStatus($"登录成功：{userName}", false);
                    await LoadTargetListsAsync(result.Session!.Token);
                    UpdatePasswordChangeUi();
                    break;
                case LoginStatus.MustChangePassword:
                    EnterMustChangePassword(result.Session, password);
                    break;
                case LoginStatus.InvalidCredentials:
                    _authenticated = false;
                    _rejectSavedCipher = true;
                    if (_openedBrowser || _mustChangePending || _awaitingNewPassword)
                        EnterAwaitingNewPassword();
                    else
                    {
                        SetStatus(result.Message, true);
                        UpdatePasswordHint();
                    }
                    StartLoginCooldown(TimeSpan.FromSeconds(5));
                    break;
                case LoginStatus.RateLimited:
                    _authenticated = false;
                    SetStatus(result.Message, true);
                    StartLoginCooldown(result.RetryAfter ?? TimeSpan.FromMinutes(1));
                    break;
                default:
                    _authenticated = false;
                    SetStatus(result.Message, true);
                    break;
            }
        }
        catch (Exception exception)
        {
            _authenticated = false;
            SetStatus("登录失败：" + exception.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadTargetListsAsync(string token)
    {
        if (_api is null) return;
        _lastToken = token;
        try
        {
            var screensTask = _api.GetScreensAsync(token);
            var layoutsTask = _api.GetLayoutsAsync(token);
            var playlistsTask = _api.GetPlaylistsAsync(token);
            var stationsTask = _api.GetWorkstationsAsync(token);
            await Task.WhenAll(screensTask, layoutsTask, playlistsTask, stationsTask);
            if (_closing || IsDisposed) return;
            _screens = await screensTask;
            _layouts = await layoutsTask;
            _playlists = await playlistsTask;
            _workstations = await stationsTask;

            FillCombo(_screenCombo, _screens.Select(row => new ScreenOption(row.Id, row.Name ?? $"画面 #{row.Id}")));
            FillCombo(_layoutCombo, _layouts.Select(row => new LayoutOption(row.Id, row.Name ?? $"布局 #{row.Id}")));
            FillCombo(_playlistCombo, _playlists.Select(row => new PlaylistOption(row.Id, row.Name ?? $"播放列表 #{row.Id}")));
            FillCombo(_workstationCombo, _workstations.Select(row =>
            {
                var layout = _layouts.FirstOrDefault(item => item.Id == row.LayoutId);
                var layoutText = row.LayoutId is null ? "未分配布局" : layout?.Name ?? $"布局 #{row.LayoutId}（未发布）";
                return new WorkstationOption(row.Id, row.Name, $"{row.Name}（{layoutText}）{(row.AllowControl ? string.Empty : "，只监视")}");
            }));

            _targetListsLoaded = true;
            ApplyTargetListLoadStatus();
        }
        catch (ApiException exception)
        {
            if (_closing || IsDisposed) return;
            _authenticated = false;
            SetStatus(exception.Message, true);
        }
        catch (Exception exception)
        {
            if (_closing || IsDisposed) return;
            _authenticated = false;
            SetStatus("加载列表失败：" + exception.Message, true);
        }
    }

    private static void FillCombo<T>(ComboBox combo, IEnumerable<T> items) where T : IOption
    {
        var selectedId = (combo.SelectedItem as IOption)?.Id;
        var list = items.ToList();
        combo.BeginUpdate();
        combo.Items.Clear();
        foreach (var item in list) combo.Items.Add(item);
        combo.EndUpdate();
        var index = selectedId is int id ? list.FindIndex(item => item.Id == id) : -1;
        combo.SelectedIndex = index >= 0 ? index : list.Count > 0 ? 0 : -1;
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        if (TrySave()) Close();
    }

    private bool TrySave()
    {
        if (_busy) return false;
        try
        {
            if (_mustChangePending)
            {
                SetStatus(DesktopSettingsCopy.MustChangeLoginStatus, true);
                SetLoginStatus(DesktopSettingsCopy.MustChangeLoginStatus, true);
                return false;
            }
            if (!SettingsValidator.TryNormalizeServerUrl(_serverUrl.Text, out var url, out var error))
            {
                SetStatus(error, true);
                return false;
            }
            var userName = _userName.Text.Trim();
            if (userName.Length == 0)
            {
                SetStatus("请填写用户名", true);
                return false;
            }
            var kind = CurrentKind();
            var screenId = (_screenCombo.SelectedItem as ScreenOption)?.Id;
            var layoutId = (_layoutCombo.SelectedItem as LayoutOption)?.Id;
            var playlistId = (_playlistCombo.SelectedItem as PlaylistOption)?.Id;
            var station = _workstationCombo.SelectedItem as WorkstationOption;

            var connectionChanged = !string.Equals(url, _originalServerUrl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(userName, _originalUserName ?? string.Empty, StringComparison.Ordinal)
                || _password.Text.Length > 0;
            if (kind == "screen" && _monitorOnly.Checked && station is not null)
            {
                var bound = _workstations.FirstOrDefault(row => row.Id == station.Id);
                if (bound is { AllowControl: true })
                {
                    SetStatus("监视模式只能绑定只允许监视的工作站，请改选或先在服务器上将此工作站设为只监视。", true);
                    return false;
                }
            }

            var targetChanged = kind != _originalKind
                || kind == "screen" && screenId != _originalScreenId
                || kind == "layout" && layoutId != _originalLayoutId
                || kind == "playlist" && playlistId != _originalPlaylistId
                || (kind == "workstation" || kind == "screen" && _monitorOnly.Checked) && station?.Id != _originalWorkstationId;
            if ((connectionChanged || targetChanged) && !_authenticated)
            {
                SetStatus("连接信息或显示目标已修改，请先登录并加载列表。", true);
                return false;
            }

            switch (kind)
            {
                case "screen" when screenId is not > 0:
                    SetStatus("请选择已发布的组态画面", true);
                    return false;
                case "layout" when layoutId is not > 0:
                    SetStatus("请选择已发布的运行布局", true);
                    return false;
                case "playlist" when playlistId is not > 0:
                    SetStatus("请选择已保存的 Kiosk 播放列表", true);
                    return false;
                case "workstation" when station is null:
                    SetStatus("请选择工作站", true);
                    return false;
                case "workstation":
                    var stationLayoutId = _workstations.FirstOrDefault(row => row.Id == station!.Id)?.LayoutId;
                    if (stationLayoutId is null || _layouts.All(item => item.Id != stationLayoutId))
                    {
                        SetStatus("该工作站未绑定已发布的运行布局，请先在运行布局页面分配并发布布局。", true);
                        return false;
                    }
                    break;
            }

            if (_password.Text.Length > 0) _plainPassword = _password.Text;
            if ((_savedCipherStale || _passwordChanged) && string.IsNullOrEmpty(_plainPassword))
            {
                SetStatus(DesktopSettingsCopy.SavedCipherStaleHint, true);
                SetLoginStatus(DesktopSettingsCopy.SavedCipherStaleHint, true);
                return false;
            }
            var passwordCipher = !_savePassword.Checked
                ? null
                : (_savedCipherStale || _passwordChanged)
                    ? PasswordProtector.Protect(_plainPassword!)
                    : string.IsNullOrEmpty(_plainPassword) ? _existingCipher : PasswordProtector.Protect(_plainPassword);
            var settings = new DesktopSettings
            {
                ServerUrl = url,
                UserName = userName,
                SavePassword = _savePassword.Checked,
                PasswordCipher = passwordCipher,
                Target = new DesktopTarget
                {
                    Kind = kind,
                    ScreenId = kind == "screen" ? screenId : null,
                    LayoutId = kind == "layout" ? layoutId : null,
                    PlaylistId = kind == "playlist" ? playlistId : null,
                    LayoutMonitorCount = kind == "layout" ? LayoutMonitorCount() : null,
                    LayoutMonitorDeviceNames = kind == "layout" ? CurrentLayoutMonitorDevices() : null,
                    WorkstationId = kind == "workstation" || kind == "screen" && _monitorOnly.Checked ? station?.Id : null,
                    WorkstationName = kind == "workstation" || kind == "screen" && _monitorOnly.Checked ? station?.Name : null,
                    TargetName = kind == "playlist" ? (_playlistCombo.SelectedItem as PlaylistOption)?.Label : kind == "workspace" ? "多页面工作台" : null,
                    MonitorOnly = (kind == "screen" || kind == "playlist") && (_monitorOnly.Checked
                        || kind == "playlist" && _playlists.Any(row => row.Id == playlistId && row.MonitorOnly))
                },
                Display = new DesktopDisplay
                {
                    FullScreen = _fullScreen.Checked,
                    MonitorDeviceName = (_monitorCombo.SelectedItem as MonitorOption)?.DeviceName
                },
                KeepAwake = _keepAwake.Checked,
                AutoStart = _autoStart.Checked,
                AllowDownloads = _allowDownloads.Checked,
                DownloadDirectory = string.IsNullOrWhiteSpace(_downloadDirectory.Text) ? null : _downloadDirectory.Text.Trim(),
                ClientId = _existingClientId,
                ManagedAutostart = _managedAutostart,
                ConfigScope = _existingScope,
                LockedFields = _lockedFields
            };

            var validation = SettingsValidator.Validate(settings, requirePassword: true);
            if (validation is not null)
            {
                SetStatus(validation, true);
                return false;
            }

            // 机器锁定的字段保留用户层原值：界面显示的是合并结果，整份保存会让管理员
            // 下发的 serverUrl/target 沉淀成用户自己的设置（撤掉策略后终端仍按旧目标跑）。
            var userLayer = _store.Load(out _);
            if (userLayer is null)
            {
                _store.Save(settings);
            }
            else
            {
                DesktopConfigLayers.ApplyEditableFields(userLayer, settings, _lockedFields);
                _store.Save(userLayer);
            }
            var autostart = AutostartRegistration.Apply(settings.AutoStart, settings.ManagedAutostart);
            if (autostart.Warning is { Length: > 0 } warning)
            {
                MessageBox.Show(this, warning, "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            SavedSettings = settings;
            DialogResult = DialogResult.OK;
            return true;
        }
        catch (Exception exception)
        {
            SetStatus("保存失败：" + exception.Message, true);
            return false;
        }
    }

    private void StartLoginCooldown(TimeSpan duration)
    {
        _loginCooldownUntil = DateTime.UtcNow + duration;
        _cooldownTimer.Start();
        UpdateButtons();
    }

    private void CooldownTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing || IsDisposed) return;
        var remaining = _loginCooldownUntil - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _cooldownTimer.Stop();
            _loginButton.Text = "登录并加载列表";
            UpdateButtons();
            return;
        }
        _loginButton.Text = $"登录并加载列表（{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}s）";
    }

    private void SetBusy(bool busy)
    {
        if (_closing || IsDisposed) return;
        _busy = busy;
        UseWaitCursor = busy;
        _serverUrl.Enabled = !busy && !IsLocked("serverUrl");
        _userName.Enabled = !busy && !IsLocked("userName");
        _password.Enabled = !busy;
        _savePassword.Enabled = !busy;
        _currentPassword.Enabled = !busy;
        _newPassword.Enabled = !busy;
        _confirmPassword.Enabled = !busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (_closing || IsDisposed) return;
        _connectButton.Enabled = !_busy;
        _loginButton.Enabled = !_busy && DateTime.UtcNow >= _loginCooldownUntil;
        _saveButton.Enabled = !_busy && !_mustChangePending;
        _changePasswordButton.Enabled = !_busy && _mustChangePending && !_awaitingNewPassword;
        _openBrowserButton.Enabled = !_busy;
        _browserDoneButton.Enabled = !_busy && (_mustChangePending || _openedBrowser) && !_passwordChanged;
    }

    private void ApplyTargetListLoadStatus()
    {
        var status = DesktopSettingsCopy.DescribeTargetListLoad(
            CurrentKind(), _screens.Count, _layouts.Count, _playlists.Count, _workstations.Count);
        SetStatus(status.Message, status.IsError);
    }

    private void SetStatus(string message, bool error)
    {
        if (_closing || IsDisposed) return;
        _status.Text = message;
        _status.ForeColor = error ? Color.FromArgb(180, 60, 60) : Color.FromArgb(50, 120, 80);
        SetLoginStatus(message, error);
    }

    private void SetLoginStatus(string message, bool error)
    {
        if (_closing || IsDisposed) return;
        _loginStatus.Text = message;
        _loginStatus.ForeColor = error ? Color.FromArgb(180, 60, 60) : Color.FromArgb(50, 120, 80);
    }

    private async Task ProbeSiteInfoAsync()
    {
        if (!SettingsValidator.TryNormalizeServerUrl(_serverUrl.Text, out var url, out _)) return;
        try
        {
            using var probe = new ServerApiClient(url);
            var info = await probe.GetSiteInfoAsync();
            if (_closing || IsDisposed) return;
            ApplySiteInfo(info);
        }
        catch
        {
            // 打开时探测失败不打断设置；操作员仍可点“测试连接”。
        }
    }

    private void ApplySiteInfo(SiteInfo info)
    {
        _demoMode = info.DemoMode;
        _siteInfo.Text = $"已连接：{info.SiteName}{(info.DemoMode ? "（演示模式）" : string.Empty)}";
        _notice.Text = DesktopSettingsCopy.Notice(info.SiteName, info.DemoMode);
        _demoNotice.Text = DesktopSettingsCopy.DemoPasswordHint;
        _demoNotice.Visible = DesktopSettingsCopy.ShowDemoPasswordHint(_demoMode, _mustChangePending);
        UpdateAccountCopy();
    }

    private void UpdateAccountCopy()
    {
        _accountHint.Text = DesktopSettingsCopy.AccountHint(CurrentKind());
        UpdatePasswordHint();
        UpdatePasswordChangeUi();
    }

    private void UpdatePasswordHint()
    {
        _passwordHint.Text = DesktopSettingsCopy.PasswordHint(
            !string.IsNullOrWhiteSpace(_existingCipher),
            _savedCipherStale,
            _mustChangePending,
            _passwordChanged,
            _rejectSavedCipher || _awaitingNewPassword);
        if (_awaitingNewPassword)
            _passwordHint.Text = DesktopSettingsCopy.AwaitingNewPasswordHint;
        _demoNotice.Visible = DesktopSettingsCopy.ShowDemoPasswordHint(_demoMode, _mustChangePending);
        if (_kindWorkspace.Checked)
            _workspaceHint.Text = DesktopSettingsCopy.WorkspaceHintText(_mustChangePending);
    }

    private void UpdatePasswordChangeUi()
    {
        var showFields = _mustChangePending && !_awaitingNewPassword;
        var showBrowserActions = showFields || _awaitingNewPassword || _openedBrowser;
        _currentPassword.Visible = showFields;
        _newPassword.Visible = showFields;
        _confirmPassword.Visible = showFields;
        _changePasswordButton.Visible = showFields;
        _openBrowserButton.Visible = showBrowserActions;
        _browserDoneButton.Visible = showFields || (_openedBrowser && !_passwordChanged && !_awaitingNewPassword);
        _browserHint.Visible = _openedBrowser || _awaitingNewPassword;
        if (_currentPassword.Parent is { } changeParent)
        {
            foreach (Control child in changeParent.Controls)
            {
                if (child is Label label && (label.Text == "当前密码" || label.Text == "新密码" || label.Text == "确认新密码"))
                    label.Visible = showFields;
            }
        }
        if (showFields && _currentPassword.Text.Length == 0)
        {
            var current = ResolveCurrentPassword(allowSaved: DesktopSettingsCopy.CanReuseSavedCipher(_savedCipherStale, _rejectSavedCipher));
            if (!string.IsNullOrEmpty(current)) _currentPassword.Text = current;
            else if (_password.Text.Length > 0) _currentPassword.Text = _password.Text;
        }
        UpdateButtons();
        UpdatePasswordHint();
    }

    private void EnterMustChangePassword(SessionData? session, string password)
    {
        _authenticated = false;
        _mustChangePending = true;
        _awaitingNewPassword = false;
        _rejectSavedCipher = false;
        _savedCipherStale = false;
        _passwordChanged = false;
        _pendingSession = session;
        if (session is not null) _lastToken = session.Token;
        _plainPassword = password;
        if (_password.Text.Length == 0) _password.Text = password;
        _currentPassword.Text = password;
        TopMost = false;
        SetStatus(DesktopSettingsCopy.MustChangeLoginStatus, true);
        _status.ForeColor = Color.FromArgb(170, 110, 20);
        _loginStatus.ForeColor = Color.FromArgb(170, 110, 20);
        UpdatePasswordChangeUi();
    }

    private void EnterAwaitingNewPassword()
    {
        _authenticated = false;
        _mustChangePending = false;
        _awaitingNewPassword = true;
        _savedCipherStale = true;
        _rejectSavedCipher = true;
        _plainPassword = null;
        _password.Clear();
        _currentPassword.Clear();
        _newPassword.Clear();
        _confirmPassword.Clear();
        TopMost = false;
        SetStatus(DesktopSettingsCopy.AwaitingNewPasswordHint, true);
        _status.ForeColor = Color.FromArgb(170, 110, 20);
        _loginStatus.ForeColor = Color.FromArgb(170, 110, 20);
        UpdatePasswordChangeUi();
    }

    private string ResolveCurrentPassword(bool allowSaved)
    {
        if (_currentPassword.Text.Length > 0) return _currentPassword.Text;
        if (_password.Text.Length > 0) return _password.Text;
        if (!string.IsNullOrEmpty(_plainPassword)) return _plainPassword;
        if (allowSaved && string.Equals(_userName.Text.Trim(), _originalUserName ?? string.Empty, StringComparison.Ordinal))
            return PasswordProtector.Unprotect(_existingCipher) ?? string.Empty;
        return string.Empty;
    }

    private void OpenBrowserButton_Click(object? sender, EventArgs e)
    {
        if (!SettingsValidator.TryNormalizeServerUrl(_serverUrl.Text, out var url, out var error))
        {
            SetStatus(error, true);
            return;
        }
        var loginUrl = DesktopSettingsCopy.LoginPageUrl(url);
        _openedBrowser = true;
        TopMost = false;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = loginUrl, UseShellExecute = true });
            _browserHint.Text = DesktopSettingsCopy.BrowserHint(loginUrl);
            SetLoginStatus(DesktopSettingsCopy.BrowserHint(loginUrl), false);
            UpdatePasswordChangeUi();
        }
        catch (Exception exception)
        {
            _browserHint.Text = $"无法打开浏览器：{exception.Message}。请手动访问 {loginUrl}，改密后回到本窗口输入新密码再登录。";
            SetStatus(_browserHint.Text, true);
        }
    }

    private void BrowserDoneButton_Click(object? sender, EventArgs e)
    {
        _openedBrowser = true;
        TopMost = false;
        EnterAwaitingNewPassword();
    }

    private async void ChangePasswordButton_Click(object? sender, EventArgs e)
    {
        if (!SettingsValidator.TryNormalizeServerUrl(_serverUrl.Text, out var url, out var error))
        {
            SetStatus(error, true);
            return;
        }
        var userName = _userName.Text.Trim();
        if (userName.Length == 0)
        {
            SetStatus("请填写用户名", true);
            return;
        }
        var current = ResolveCurrentPassword(allowSaved: DesktopSettingsCopy.CanReuseSavedCipher(_savedCipherStale, _rejectSavedCipher));
        var validation = DesktopSettingsCopy.ValidateNewPassword(current, _newPassword.Text, _confirmPassword.Text);
        if (validation is not null)
        {
            SetStatus(validation, true);
            return;
        }

        SetBusy(true);
        try
        {
            _api?.Dispose();
            _api = new ServerApiClient(url);
            var token = _pendingSession?.Token ?? _lastToken;
            if (string.IsNullOrEmpty(token))
            {
                var login = await _api.LoginAsync(userName, current);
                if (_closing || IsDisposed) return;
                if (login.Status is not (LoginStatus.Success or LoginStatus.MustChangePassword) || login.Session is null)
                {
                    SetStatus(login.Message, true);
                    if (login.Status == LoginStatus.InvalidCredentials) StartLoginCooldown(TimeSpan.FromSeconds(5));
                    if (login.Status == LoginStatus.RateLimited) StartLoginCooldown(login.RetryAfter ?? TimeSpan.FromMinutes(1));
                    return;
                }
                _pendingSession = login.Session;
                token = login.Session.Token;
            }

            var result = await _api.ChangePasswordAsync(token, current, _newPassword.Text);
            if (_closing || IsDisposed) return;
            if (result.Status != LoginStatus.Success || result.Session is null)
            {
                SetStatus(result.Message, true);
                return;
            }

            _pendingSession = result.Session;
            _lastToken = result.Session.Token;
            _plainPassword = _newPassword.Text;
            _password.Text = _newPassword.Text;
            _mustChangePending = false;
            _awaitingNewPassword = false;
            _savedCipherStale = true;
            _passwordChanged = true;
            _serverPasswordUpdated = true;
            _authenticated = true;
            SetStatus("密码已修改，请先点“保存并启动”，把新密码写入本机。正在加载列表。", false);
            await LoadTargetListsAsync(result.Session.Token);
            UpdatePasswordChangeUi();
        }
        catch (Exception exception)
        {
            SetStatus("修改密码失败：" + exception.Message, true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenLogsDirectory()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _paths.LogsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            SetStatus("无法打开日志目录：" + exception.Message, true);
        }
    }

    private interface IOption
    {
        int Id { get; }
    }

    private sealed record MonitorOption(string DeviceName, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ScreenOption(int Id, string Label) : IOption
    {
        public override string ToString() => Label;
    }

    private sealed record PlaylistOption(int Id, string Label) : IOption
    {
        public override string ToString() => Label;
    }

    private sealed record LayoutOption(int Id, string Label) : IOption
    {
        public override string ToString() => Label;
    }

    private sealed record WorkstationOption(int Id, string Name, string Label) : IOption
    {
        public override string ToString() => Label;
    }
}
