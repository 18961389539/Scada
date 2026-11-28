using System.Security.Principal;
using System.Text.Json;

namespace Scada.Desktop.Configuration;

/// <summary>
/// 集中配准命令行。机器级只写非机密字段；用户级可从未附加到命令行的标准输入读取密码，
/// 并用 DPAPI CurrentUser 加密保存。脚本通过退出码判断结果；WinExe 的控制台输出仅作辅助。
/// </summary>
internal static class ProvisionCommand
{
    public static bool IsRequested(string[] args) =>
        args.Any(arg => string.Equals(arg, "--provision", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args, DesktopPaths paths)
    {
        try
        {
            if (args.Any(arg => string.Equals(arg, "--password", StringComparison.OrdinalIgnoreCase)
                || arg.StartsWith("--password=", StringComparison.OrdinalIgnoreCase)))
            {
                return Fail("禁止在命令行传递密码，请使用 --password-stdin");
            }

            var configPath = ValueOf(args, "--config");
            if (string.IsNullOrWhiteSpace(configPath))
                return Fail("--config 必填，指向配准 JSON 文件");
            if (!File.Exists(configPath))
                return Fail("配准文件不存在：" + configPath);

            var scope = (ValueOf(args, "--scope") ?? "user").Trim().ToLowerInvariant();
            if (scope is not ("machine" or "user"))
                return Fail("--scope 只支持 machine 或 user");
            var passwordFromStdin = args.Any(arg => string.Equals(arg, "--password-stdin", StringComparison.OrdinalIgnoreCase));
            if (scope == "machine" && passwordFromStdin)
                return Fail("机器级配准不能写密码：DPAPI CurrentUser 密文无法被其他账户解密");

            // 提权运行的用户级配准会写进管理员自己的 %LOCALAPPDATA%，目标操作员账户仍然没有配置。
            // 现场表现为"脚本报成功，客户端还是没登录"，因此默认拒绝，需显式确认。
            var allowElevated = args.Any(arg => string.Equals(arg, "--allow-elevated-user", StringComparison.OrdinalIgnoreCase));
            if (ShouldRefuseUserScope(scope, IsElevated(), allowElevated))
                return Fail("当前以管理员身份运行：--scope user 会写入管理员账户的配置。请用目标账户运行，或显式加 --allow-elevated-user");

            var machine = JsonSerializer.Deserialize<MachineSettings>(File.ReadAllText(configPath), MachineSettings.JsonOptions);
            if (machine is null) return Fail("配准文件内容为空或格式不正确");

            return scope == "machine"
                ? WriteMachine(machine, paths)
                : WriteUser(machine, paths, passwordFromStdin);
        }
        catch (Exception exception)
        {
            DesktopLogger.Error("集中配准失败", exception);
            return Fail("配准失败：" + exception.Message);
        }
    }

    private static int WriteMachine(MachineSettings machine, DesktopPaths paths)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return Fail("机器级配准需要管理员权限");

        if (!SettingsValidator.TryNormalizeServerUrl(machine.ServerUrl, out var serverUrl, out var urlError))
            return Fail(urlError);
        machine.ServerUrl = serverUrl;

        if (machine.Target is not null)
        {
            var targetError = SettingsValidator.Validate(new DesktopSettings
            {
                ServerUrl = serverUrl,
                Target = machine.Target,
                SavePassword = false
            }, requirePassword: false);
            if (targetError is not null) return Fail(targetError);
        }
        if (machine.Locked is not { Length: > 0 }) machine.Locked = DesktopConfigResolver.DefaultLocks(machine);

        new MachineSettingsStore(paths.MachineSettingsFile).Save(machine);
        Console.WriteLine("机器配置已写入：" + paths.MachineSettingsFile);
        return 0;
    }

    private static int WriteUser(MachineSettings provision, DesktopPaths paths, bool passwordFromStdin)
    {
        var store = new SettingsStore(paths.SettingsFile);
        var existing = store.Load(out var loadError);
        if (existing is null && loadError is not null && File.Exists(paths.SettingsFile))
            return Fail("现有用户配置无法读取：" + loadError);

        var settings = existing ?? new DesktopSettings();
        if (!string.IsNullOrWhiteSpace(provision.ServerUrl)) settings.ServerUrl = provision.ServerUrl;
        if (!string.IsNullOrWhiteSpace(provision.UserName)) settings.UserName = provision.UserName;
        if (provision.Target is not null) settings.Target = Copy(provision.Target);
        if (provision.Display is not null) settings.Display = Copy(provision.Display);
        if (provision.AutoStart is bool autoStart) settings.AutoStart = autoStart;
        if (provision.AllowDownloads is bool allowDownloads) settings.AllowDownloads = allowDownloads;
        if (!string.IsNullOrWhiteSpace(provision.DownloadDirectory)) settings.DownloadDirectory = provision.DownloadDirectory.Trim();

        if (passwordFromStdin)
        {
            var password = Console.In.ReadLine();
            if (string.IsNullOrEmpty(password)) return Fail("标准输入没有读取到密码");
            settings.PasswordCipher = PasswordProtector.Protect(password);
            settings.SavePassword = true;
        }

        var validation = SettingsValidator.Validate(settings, requirePassword: false);
        if (validation is not null) return Fail(validation);
        if (!Guid.TryParse(settings.ClientId, out _)) settings.ClientId = Guid.NewGuid().ToString("D");

        store.Save(settings);
        Console.WriteLine("用户配置已写入：" + paths.SettingsFile);
        return 0;
    }

    private static DesktopTarget Copy(DesktopTarget source) => new()
    {
        Kind = source.Kind,
        ScreenId = source.ScreenId,
        LayoutId = source.LayoutId,
        PlaylistId = source.PlaylistId,
        LayoutMonitorCount = source.LayoutMonitorCount,
        LayoutMonitorDeviceNames = source.LayoutMonitorDeviceNames,
        WorkstationId = source.WorkstationId,
        WorkstationName = source.WorkstationName,
        TargetName = source.TargetName,
        MonitorOnly = source.MonitorOnly
    };

    private static DesktopDisplay Copy(DesktopDisplay source) => new()
    {
        FullScreen = source.FullScreen,
        MonitorDeviceName = source.MonitorDeviceName
    };

    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : null;
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(name.Length + 1)..];
        }
        return null;
    }

    internal static bool ShouldRefuseUserScope(string scope, bool elevated, bool allowElevated) =>
        scope == "user" && elevated && !allowElevated;

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        DesktopLogger.Error("集中配准：" + message);
        return 2;
    }
}
