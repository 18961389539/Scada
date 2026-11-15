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
    private readonly string? _existingCipher;
    private readonly string _originalServerUrl;
    private readonly string? _originalUserName;
    private readonly string _originalKind;
    private readonly int? _originalScreenId;
    private readonly int? _originalLayoutId;
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
    private readonly RadioButton _kindWorkstation = new() { Text = "按工作站自动加载", AutoSize = true };
    private readonly ComboBox _screenCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly ComboBox _layoutCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly ComboBox _workstationCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly CheckBox _monitorOnly = new() { Text = "监视模式（只读，不显示操作控件）", AutoSize = true, Checked = true };
    private readonly CheckBox _fullScreen = new() { Text = "全屏运行", AutoSize = true, Checked = true };
    private readonly ComboBox _monitorCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly CheckBox _autoStart = new() { Text = "Windows 登录后自动启动", AutoSize = true };
    private readonly CheckBox _keepAwake = new() { Text = "阻止系统休眠和关闭显示器", AutoSize = true, Checked = true };
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
    private IReadOnlyList<ScreenSummary> _screens = [];
    private IReadOnlyList<LayoutSummary> _layouts = [];
    private IReadOnlyList<WorkstationSummary> _workstations = [];

    public SettingsForm(DesktopPaths paths, SettingsStore store, DesktopSettings? existing, string? initialError)
    {
        _paths = paths;
        _store = store;
        _existingCipher = existing?.PasswordCipher;
        _originalServerUrl = existing?.ServerUrl ?? "http://localhost:5080";
        _originalUserName = existing?.UserName;
        _originalKind = existing?.Target.Kind ?? "screen";
        _originalScreenId = existing?.Target.ScreenId;
        _originalLayoutId = existing?.Target.LayoutId;
        _originalWorkstationId = existing?.Target.WorkstationId;

        Text = existing is null ? "SCADA 桌面客户端 - 首次配置" : "SCADA 桌面客户端 - 设置";
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        ClientSize = new Size(760, 850);

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
        _kindScreen.CheckedChanged += (_, _) => UpdateTargetControls();
        _kindLayout.CheckedChanged += (_, _) => UpdateTargetControls();
        _kindWorkstation.CheckedChanged += (_, _) => UpdateTargetControls();
        _serverUrl.TextChanged += (_, _) => MarkConnectionDirty();
        _userName.TextChanged += (_, _) => MarkConnectionDirty();
        _password.TextChanged += (_, _) => MarkConnectionDirty();
        _cooldownTimer.Tick += CooldownTimer_Tick;

        UpdateTargetControls();
        UpdateButtons();
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
        for (var i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildNotice(initialError), 0, 0);
        root.Controls.Add(BuildConnectionGroup(), 0, 1);
        root.Controls.Add(BuildAccountGroup(), 0, 2);
        root.Controls.Add(BuildTargetGroup(), 0, 3);
        root.Controls.Add(BuildOptionsGroup(), 0, 4);
        root.Controls.Add(BuildButtons(), 0, 5);
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
        kinds.Controls.Add(_kindWorkstation);
        table.Controls.Add(kinds, 0, 0);
        table.SetColumnSpan(kinds, 3);

        table.Controls.Add(NewLabel("组态画面"), 0, 1);
        table.Controls.Add(_screenCombo, 1, 1);
        table.Controls.Add(NewLabel("运行布局"), 0, 2);
        table.Controls.Add(_layoutCombo, 1, 2);
        table.Controls.Add(NewLabel("工作站"), 0, 3);
        table.Controls.Add(_workstationCombo, 1, 3);
        table.Controls.Add(_monitorOnly, 1, 4);
        table.SetColumnSpan(_monitorOnly, 2);
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
            case "workstation": _kindWorkstation.Checked = true; break;
            default: _kindScreen.Checked = true; break;
        }
        _monitorOnly.Checked = existing.Target.MonitorOnly;
        _fullScreen.Checked = existing.Display.FullScreen;
        _keepAwake.Checked = existing.KeepAwake;
        _autoStart.Checked = existing.AutoStart;
        _savePassword.Checked = existing.SavePassword;
        _serverUrl.Text = existing.ServerUrl;
        _userName.Text = existing.UserName ?? string.Empty;

        if (existing.Target.ScreenId is int screenId) _screenCombo.Items.Add(new ScreenOption(screenId, $"当前画面 #{screenId}（登录后刷新名称）"));
        if (existing.Target.LayoutId is int layoutId) _layoutCombo.Items.Add(new LayoutOption(layoutId, $"当前布局 #{layoutId}（登录后刷新名称）"));
        if (existing.Target.WorkstationId is int stationId)
        {
            _workstationCombo.Items.Add(new WorkstationOption(stationId, existing.Target.WorkstationName ?? $"工作站 #{stationId}", $"当前工作站 #{stationId}（登录后刷新名称）"));
        }
        _screenCombo.SelectedIndex = _screenCombo.Items.Count > 0 ? 0 : -1;
        _layoutCombo.SelectedIndex = _layoutCombo.Items.Count > 0 ? 0 : -1;
        _workstationCombo.SelectedIndex = _workstationCombo.Items.Count > 0 ? 0 : -1;
    }

    private void UpdateTargetControls()
    {
        var kind = CurrentKind();
        _screenCombo.Enabled = kind == "screen";
        _monitorOnly.Enabled = kind == "screen";
        _layoutCombo.Enabled = kind == "layout";
        _workstationCombo.Enabled = kind == "workstation";
    }

    private string CurrentKind() =>
        _kindLayout.Checked ? "layout" : _kindWorkstation.Checked ? "workstation" : "screen";

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
        try
        {
            var screensTask = _api.GetScreensAsync(token);
            var layoutsTask = _api.GetLayoutsAsync(token);
            var stationsTask = _api.GetWorkstationsAsync(token);
            await Task.WhenAll(screensTask, layoutsTask, stationsTask);
            if (_closing || IsDisposed) return;
            _screens = await screensTask;
            _layouts = await layoutsTask;
            _workstations = await stationsTask;

            FillCombo(_screenCombo, _screens.Select(row => new ScreenOption(row.Id, row.Name ?? $"画面 #{row.Id}")));
            FillCombo(_layoutCombo, _layouts.Select(row => new LayoutOption(row.Id, row.Name ?? $"布局 #{row.Id}")));
            FillCombo(_workstationCombo, _workstations.Select(row =>
            {
                var layout = _layouts.FirstOrDefault(item => item.Id == row.LayoutId);
                var layoutText = row.LayoutId is null ? "未分配布局" : layout?.Name ?? $"布局 #{row.LayoutId}（未发布）";
                return new WorkstationOption(row.Id, row.Name, $"{row.Name}（{layoutText}）{(row.AllowControl ? string.Empty : "，只监视")}");
            }));

            if (_screens.Count == 0) SetStatus("当前账号看不到已发布画面，请先在组态编辑器中发布画面。", true);
            else if (_layouts.Count == 0 && _workstations.Count == 0) SetStatus("当前账号看不到已发布布局或工作站，可使用单个画面模式。", true);
            else SetStatus($"已加载 {_screens.Count} 个画面、{_layouts.Count} 个布局、{_workstations.Count} 个工作站。", false);
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
            var station = _workstationCombo.SelectedItem as WorkstationOption;

            var connectionChanged = !string.Equals(url, _originalServerUrl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(userName, _originalUserName ?? string.Empty, StringComparison.Ordinal)
                || _password.Text.Length > 0;
            var targetChanged = kind != _originalKind
                || kind == "screen" && screenId != _originalScreenId
                || kind == "layout" && layoutId != _originalLayoutId
                || kind == "workstation" && station?.Id != _originalWorkstationId;
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
                    WorkstationId = kind == "workstation" ? station!.Id : null,
                    WorkstationName = kind == "workstation" ? station!.Name : null,
                    MonitorOnly = kind == "screen" && _monitorOnly.Checked
                },
                Display = new DesktopDisplay
                {
                    FullScreen = _fullScreen.Checked,
                    MonitorDeviceName = (_monitorCombo.SelectedItem as MonitorOption)?.DeviceName
                },
                KeepAwake = _keepAwake.Checked,
                AutoStart = _autoStart.Checked
            };

            var validation = SettingsValidator.Validate(settings, requirePassword: true);
            if (validation is not null)
            {
                SetStatus(validation, true);
                return;
            }

            _store.Save(settings);
            AutostartRegistration.Apply(settings.AutoStart, out var warning);
            if (!string.IsNullOrWhiteSpace(warning))
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
        _serverUrl.Enabled = !busy;
        _userName.Enabled = !busy;
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

    private sealed record LayoutOption(int Id, string Label) : IOption
    {
        public override string ToString() => Label;
    }

    private sealed record WorkstationOption(int Id, string Name, string Label) : IOption
    {
        public override string ToString() => Label;
    }
}
