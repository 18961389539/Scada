using System.Text.Json;

namespace Scada.Desktop.Configuration;

/// <summary>
/// settings.json 的读取与原子写入。损坏文件不直接覆盖：先备份成带时间戳的副本，
/// 现场还能从里面人工找回服务器地址或目标选择。
/// </summary>
internal sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public DesktopSettings? Load(out string? error)
    {
        error = null;
        if (!File.Exists(Path))
        {
            error = "尚未配置桌面客户端";
            return null;
        }
        try
        {
            var json = File.ReadAllText(Path);
            var settings = JsonSerializer.Deserialize<DesktopSettings>(json, DesktopSettings.JsonOptions);
            if (settings is null)
            {
                error = "配置文件内容为空";
                return null;
            }
            var validation = SettingsValidator.Validate(settings, requirePassword: false);
            if (validation is not null)
            {
                error = validation;
                return null;
            }
            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = "配置文件无法读取，已备份为 .corrupt 文件";
            DesktopLogger.Error("读取配置失败", exception);
            TryBackupCorrupt();
            return null;
        }
    }

    public void Save(DesktopSettings settings)
    {
        settings.Version = 1;
        settings.ServerUrl = settings.ServerUrl.Trim();
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(settings, DesktopSettings.JsonOptions);
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
            if (File.Exists(Path))
            {
                var backup = Path + ".bak";
                File.Replace(temp, Path, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, Path);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 清理失败不影响主流程 */ }
        }
    }

    private void TryBackupCorrupt()
    {
        try
        {
            if (!File.Exists(Path)) return;
            var target = Path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(Path, target, overwrite: false);
        }
        catch
        {
            // 备份失败时保留原文件，交给用户手工处理。
        }
    }
}
