using System.Text.Json;

namespace Scada.Desktop.Configuration;

/// <summary>
/// 机器配置读取与原子写入。客户端通常只读；写入由管理员执行 --provision --scope machine。
/// </summary>
internal sealed class MachineSettingsStore(string path)
{
    public string Path { get; } = path;

    public MachineSettings? Load(out string? error)
    {
        error = null;
        if (!File.Exists(Path)) return null;
        try
        {
            var json = File.ReadAllText(Path);
            var settings = JsonSerializer.Deserialize<MachineSettings>(json, MachineSettings.JsonOptions);
            if (settings is null)
            {
                error = "机器配置文件内容为空";
                return null;
            }
            if (settings.Version > 1)
            {
                error = "机器配置版本高于当前客户端版本";
                return null;
            }
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = "机器配置无法读取，已回退到本机用户配置";
            DesktopLogger.Error("读取机器配置失败", exception);
            return null;
        }
    }

    public void Save(MachineSettings settings)
    {
        settings.Version = 1;
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, MachineSettings.JsonOptions);
        var temp = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            var backup = Path + ".bak";
            if (File.Exists(Path)) File.Replace(temp, Path, backup, ignoreMetadataErrors: true);
            else File.Move(temp, Path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 清理失败不影响主流程 */ }
        }
    }
}

internal static class InstallManifestStore
{
    public static InstallManifest? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), MachineSettings.JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            DesktopLogger.Warn("读取安装清单失败：" + exception.Message);
            return null;
        }
    }

    public static bool IsManagedAutostart(string path) =>
        string.Equals(Load(path)?.ManagedAutostart, "task", StringComparison.OrdinalIgnoreCase);

    public static bool IsManagedAutostart(DesktopPaths paths) =>
        IsManagedAutostart(paths.InstallManifestFile) || IsManagedAutostart(paths.UserInstallManifestFile);
}
