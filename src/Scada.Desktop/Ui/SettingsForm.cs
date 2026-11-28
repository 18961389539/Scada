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
    private readonly System.Windows.Forms.Timer _cooldownTimer = new() { Interval = 1000 };

    private ServerApiClient? _api;
    private string? _plainPassword;
    private bool _authenticated;
    private bool _busy;
    private bool _closing;
    private DateTime _loginCooldownUntil = DateTime.MinValue;
    private string? _lastToken;
    private IReadOnlyList<ScreenSummary> _screens = [];
    private IReadOnlyList<LayoutSummary> _layouts = [];
    private IReadOnlyList<PlaylistSummary> _playlists = [];
    private IReadOnlyList<WorkstationSummary> _workstations = [];
    private string[]? _layoutMonitorDevices;

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

        Text = existing is null ? "SCADA 桌面客户端 - 首次配置" : "SCADA 桌面客户端 - 设置";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(760, 930);

        BuildUi(existing, initialError);
        PopulateMonitors(existing?.Display.MonitorDeviceName);
        ApplyExistingTarget(existing);
        _passwordHint.Text = string.IsNullOrWhiteSpace(_existingCipher)
            ? "尚未保存密码"
            : "已保存密码，留空表示沿用";
        _status.Text = string.Empty;

        _connectButton.Click += ConnectButton_Click;
        _loginButton.Click += LoginButton_Click;
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
    }

    public DesktopSettings? SavedSettings { get; private set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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
        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            ForeColor = Color.FromArgb(80, 100, 120),
            Text = "自动登录使用专用账号，建议只授予查看权限。密码只以 Windows DPAPI 加密保存在本机，不会写入日志。"
        };
        panel.Controls.Add(hint);
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
        var group = NewGroup("专用账号");
        var table = NewGrid(3);
        table.Controls.Add(NewLabel("用户名"), 0, 0);
        table.Controls.Add(_userName, 1, 0);
        _userName.Dock = DockStyle.Fill;
        table.Controls.Add(NewLabel("密码"), 0, 1);
        table.Controls.Add(_password, 1, 1);
        _password.Dock = DockStyle.Fill;
        table.Controls.Add(_savePassword, 1, 2);
        table.SetColumnSpan(_savePassword, 2);
        table.Controls.Add(_passwordHint, 1, 3);
        table.SetColumnSpan(_passwordHint, 2);
        table.Controls.Add(_loginButton, 2, 1);
        group.Controls.Add(table);
        return group;
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

        table.Controls.Add(NewLabel("组态画面"), 0, 1);
        table.Controls.Add(_screenCombo, 1, 1);
        table.Controls.Add(NewLabel("运行布局"), 0, 2);
        table.Controls.Add(_layoutCombo, 1, 2);
        table.Controls.Add(NewLabel("播放列表"), 0, 3);
        table.Controls.Add(_playlistCombo, 1, 3);
        table.Controls.Add(NewLabel("工作站"), 0, 4);
        table.Controls.Add(_workstationCombo, 1, 4);
        table.Controls.Add(_monitorOnly, 1, 5);
        table.SetColumnSpan(_monitorOnly, 2);
        table.Controls.Add(_layoutMonitorPanel, 1, 6);
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
        _screenCombo.Enabled = !locked && kind == "screen";
        _monitorOnly.Enabled = !locked && (kind == "screen" || kind == "playlist");
        _layoutCombo.Enabled = !locked && kind == "layout";
        _playlistCombo.Enabled = !locked && kind == "playlist";
        _workstationCombo.Enabled = !locked && (kind == "workstation" || kind == "screen" && _monitorOnly.Checked);
        _layoutMonitorPanel.Visible = kind == "layout";
        if (kind == "layout") _ = RefreshLayoutMonitorsAsync();
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
            _siteInfo.Text = $"已连接：{info.SiteName}{(info.DemoMode ? "（演示模式）" : string.Empty)}";
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
                    SetStatus($"登录成功：{userName}", false);
                    await LoadTargetListsAsync(result.Session!.Token);
                    break;
                case LoginStatus.MustChangePassword:
                    _authenticated = false;
                    SetStatus(result.Message, true);
                    break;
                case LoginStatus.InvalidCredentials:
                    _authenticated = false;
                    SetStatus(result.Message, true);
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

            if (_screens.Count == 0) SetStatus("当前账号看不到已发布画面，请先在组态编辑器中发布画面。", true);
            else if (_layouts.Count == 0 && _workstations.Count == 0) SetStatus("当前账号看不到已发布布局或工作站，可使用单个画面模式。", true);
            else SetStatus($"已加载 {_screens.Count} 个画面、{_layouts.Count} 个布局、{_playlists.Count} 个播放列表、{_workstations.Count} 个工作站。", false);
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
        if (_busy) return;
        try
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
                    return;
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
                return;
            }

            switch (kind)
            {
                case "screen" when screenId is not > 0:
                    SetStatus("请选择已发布的组态画面", true);
                    return;
                case "layout" when layoutId is not > 0:
                    SetStatus("请选择已发布的运行布局", true);
                    return;
                case "playlist" when playlistId is not > 0:
                    SetStatus("请选择已保存的 Kiosk 播放列表", true);
                    return;
                case "workstation" when station is null:
                    SetStatus("请选择工作站", true);
                    return;
                case "workstation":
                    var stationLayoutId = _workstations.FirstOrDefault(row => row.Id == station!.Id)?.LayoutId;
                    if (stationLayoutId is null || _layouts.All(item => item.Id != stationLayoutId))
                    {
                        SetStatus("该工作站未绑定已发布的运行布局，请先在运行布局页面分配并发布布局。", true);
                        return;
                    }
                    break;
            }

            var settings = new DesktopSettings
            {
                ServerUrl = url,
                UserName = userName,
                SavePassword = _savePassword.Checked,
                PasswordCipher = _savePassword.Checked
                    ? string.IsNullOrEmpty(_plainPassword) ? _existingCipher : PasswordProtector.Protect(_plainPassword)
                    : null,
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
                return;
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
            Close();
        }
        catch (Exception exception)
        {
            SetStatus("保存失败：" + exception.Message, true);
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
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (_closing || IsDisposed) return;
        _connectButton.Enabled = !_busy;
        _loginButton.Enabled = !_busy && DateTime.UtcNow >= _loginCooldownUntil;
        _saveButton.Enabled = !_busy;
    }

    private void SetStatus(string message, bool error)
    {
        if (_closing || IsDisposed) return;
        _status.Text = message;
        _status.ForeColor = error ? Color.FromArgb(180, 60, 60) : Color.FromArgb(50, 120, 80);
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
