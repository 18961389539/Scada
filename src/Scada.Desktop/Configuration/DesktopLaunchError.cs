namespace Scada.Desktop.Configuration;

/// <summary>
/// 画面进程把自动登录等配置失败原因写到本机数据目录，
/// 供守护进程在打开设置页时原样带上，避免只剩一句“请更新账号”。
/// </summary>
internal static class DesktopLaunchError
{
    public const string FileName = "last-launch-error.txt";

    public static string FilePath(DesktopPaths paths) => Path.Combine(paths.Root, FileName);

    public static void Write(DesktopPaths paths, string message)
    {
        var text = (message ?? string.Empty).Trim();
        if (text.Length == 0) return;
        if (text.Length > 2000) text = text[..2000];
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(FilePath(paths), text);
    }

    public static string? Read(DesktopPaths paths)
    {
        var file = FilePath(paths);
        if (!File.Exists(file)) return null;
        try
        {
            var text = File.ReadAllText(file).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Clear(DesktopPaths paths)
    {
        var file = FilePath(paths);
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException)
        {
            // 清不掉时下次读取仍可覆盖；不阻断启动。
        }
    }

    public static bool IsMustChangePassword(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("修改初始密码", StringComparison.Ordinal)
            || message.Contains("请先修改密码", StringComparison.Ordinal)
            || message.Contains("首次登录或密码已被重置", StringComparison.Ordinal);
    }

    public static string SupervisorPrompt(string? launchError)
    {
        if (IsMustChangePassword(launchError))
        {
            var reason = string.IsNullOrWhiteSpace(launchError)
                ? "该账号需要先修改初始密码。"
                : launchError.Trim();
            return reason + "\n\n是否打开设置，在本窗口修改初始密码或按提示用浏览器完成改密？";
        }

        if (!string.IsNullOrWhiteSpace(launchError))
            return launchError.Trim() + "\n\n画面未启动。是否打开设置检查后重试？";

        return "自动登录或配置校验失败，画面未启动。\n\n是否打开设置检查后重试？";
    }
}
