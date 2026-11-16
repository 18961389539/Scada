namespace Scada.Desktop.Configuration;

internal sealed record ResolvedDesktopConfig(DesktopSettings Settings, MachineSettings? Machine, string? Error);

/// <summary>
/// 合并用户配置与机器配置。机器值最后覆盖，确保管理员下发的目标不会被本机手工修改绕过；
/// 密码、ClientId 等机密/身份字段永远来自用户配置。
/// </summary>
internal static class DesktopConfigResolver
{
    public static ResolvedDesktopConfig? Resolve(DesktopSettings? user, MachineSettings? machine, bool managedAutostart)
    {
        if (user is null && machine is null) return null;
        var settings = user ?? new DesktopSettings();

        if (machine is not null)
        {
            if (!string.IsNullOrWhiteSpace(machine.ServerUrl)) settings.ServerUrl = machine.ServerUrl.Trim();
            if (!string.IsNullOrWhiteSpace(machine.UserName)) settings.UserName = machine.UserName.Trim();
            if (machine.Target is not null) settings.Target = Copy(machine.Target);
            if (machine.Display is not null) settings.Display = Copy(machine.Display);
            if (machine.AutoStart is bool autoStart) settings.AutoStart = autoStart;
            if (machine.AllowDownloads is bool allowDownloads) settings.AllowDownloads = allowDownloads;
            if (!string.IsNullOrWhiteSpace(machine.DownloadDirectory)) settings.DownloadDirectory = machine.DownloadDirectory.Trim();
            // JSON 显式写 "locked": null 时反序列化结果是 null，不能直接取 Length。
            settings.LockedFields = machine.Locked is { Length: > 0 } locked ? locked : DefaultLocks(machine);
            settings.ConfigScope = "machine";
        }
        else
        {
            settings.ConfigScope = user is null ? "local" : "user";
        }
        settings.ManagedAutostart = managedAutostart;

        var error = SettingsValidator.Validate(settings, requirePassword: false);
        return new ResolvedDesktopConfig(settings, machine, error);
    }

    public static string[] DefaultLocks(MachineSettings machine)
    {
        var locked = new List<string>();
        if (!string.IsNullOrWhiteSpace(machine.ServerUrl)) locked.Add("serverUrl");
        if (!string.IsNullOrWhiteSpace(machine.UserName)) locked.Add("userName");
        if (machine.Target is not null) locked.Add("target");
        if (machine.Display is not null) locked.Add("display");
        if (machine.AutoStart is not null) locked.Add("autoStart");
        if (machine.AllowDownloads is not null) locked.Add("allowDownloads");
        if (!string.IsNullOrWhiteSpace(machine.DownloadDirectory)) locked.Add("downloadDirectory");
        return locked.ToArray();
    }

    private static DesktopTarget Copy(DesktopTarget source) => new()
    {
        Kind = source.Kind,
        ScreenId = source.ScreenId,
        LayoutId = source.LayoutId,
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
}
