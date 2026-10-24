using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.History;

namespace Scada.Engine.Maintenance;

public sealed record BackupFile(string Path, long Bytes, string Sha256);
public sealed record BackupManifest(
    DateTimeOffset CreatedAt, string AppVersion, string ConfigDb,
    int Devices, int Tags, int AlarmRules, int HistoryFiles,
    IReadOnlyList<BackupFile> Files);
public sealed record BackupSummary(string FileName, string Path, long Bytes, DateTimeOffset CreatedAt);
public sealed record BackupVerification(bool Valid, IReadOnlyList<string> Problems, BackupManifest? Manifest);
public sealed record RestoreReport(string TargetRoot, int Files, int Devices, int Tags, int AlarmRules);

// 完整备份包：配置库 + 历史库 + 历史暂存 + Data Protection 密钥 + OPC PKI + 清单。
// 只做"可校验的备份"，恢复由运维在服务停止后按清单解包替换，避免运行中覆盖正在使用的库。
public sealed class BackupService(IOptions<ScadaSettings> options, ILogger<BackupService> logger, HistorySpool? spool = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ScadaSettings _settings = options.Value;
    private readonly HistorySpool _spool = spool ?? new HistorySpool(
        options.Value.HistoryRoot,
        options.Value.HistorySpoolMaxBytes,
        options.Value.HistorySpoolSegmentBytes,
        options.Value.HistorySpoolMinFreeMb,
        options.Value.HistorySpoolDropOldestOnFull);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Root => Path.GetFullPath(_settings.BackupRoot);

    public async Task<BackupSummary> CreateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var staging = Path.Combine(Root, ".staging-" + Guid.NewGuid().ToString("N"));
        var archive = staging + ".partial";
        try
        {
            Directory.CreateDirectory(staging);
            var files = new List<BackupFile>();
            var configSource = Path.GetFullPath(_settings.ConfigDbPath);
            var configCopy = Path.Combine(staging, "config.db");
            var copied = CopyDatabase(configSource, configCopy);
            if (copied) files.Add(Describe(staging, configCopy));

            var historyFiles = await CopyHistoryAsync(staging, files, cancellationToken);
            CopyDirectory(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(configSource)!, "keys")), Path.Combine(staging, "keys"), files, staging);
            if (!string.IsNullOrWhiteSpace(_settings.OpcPkiRoot))
            {
                CopyDirectory(new DirectoryInfo(_settings.OpcPkiRoot), Path.Combine(staging, "opc-pki"), files, staging);
            }

            var (devices, tags, rules) = copied ? await CountConfigurationAsync(configCopy, cancellationToken) : (0, 0, 0);
            var manifest = new BackupManifest(DateTimeOffset.UtcNow, AppVersion(), Path.GetFileName(configSource),
                devices, tags, rules, historyFiles, files);
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, Json), cancellationToken);

            var fileName = $"full_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.zip";
            var target = Path.Combine(Root, fileName);
            ZipFile.CreateFromDirectory(staging, archive, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(archive, target);
            Prune();
            logger.LogInformation("完整备份完成：{Path}（{Files} 个文件，{Bytes} 字节）", target, files.Count, new FileInfo(target).Length);
            return new BackupSummary(fileName, target, new FileInfo(target).Length, manifest.CreatedAt);
        }
        finally
        {
            _gate.Release();
            try { File.Delete(archive); } catch (Exception ex) { logger.LogWarning(ex, "清理未完成的备份包失败"); }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception ex) { logger.LogWarning(ex, "清理备份暂存目录失败：{Path}", staging); }
        }
    }

    public IReadOnlyList<BackupSummary> List()
    {
        if (!Directory.Exists(Root)) return Array.Empty<BackupSummary>();
        return Directory.EnumerateFiles(Root, "full_*.zip")
            .Select(path => new FileInfo(path))
            .OrderByDescending(x => x.Name, StringComparer.Ordinal)
            .Select(x => new BackupSummary(x.Name, x.FullName, x.Length, x.LastWriteTimeUtc))
            .ToList();
    }

    public async Task<BackupVerification> VerifyAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = Resolve(fileName);
        var extract = Path.Combine(Path.GetTempPath(), "scada-backup-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(extract);
            ZipFile.ExtractToDirectory(path, extract);
            var manifestPath = Path.Combine(extract, "manifest.json");
            if (!File.Exists(manifestPath)) return new BackupVerification(false, ["缺少 manifest.json"], null);

            var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), Json);
            if (manifest is null) return new BackupVerification(false, ["manifest.json 无法解析"], null);

            var problems = new List<string>();
            foreach (var file in manifest.Files)
            {
                var full = Path.Combine(extract, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) { problems.Add($"缺少文件：{file.Path}"); continue; }
                var info = new FileInfo(full);
                if (info.Length != file.Bytes) { problems.Add($"大小不符：{file.Path}（清单 {file.Bytes}，实际 {info.Length}）"); continue; }
                if (!string.Equals(Hash(full), file.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"哈希不符：{file.Path}");
            }

            var configCopy = Path.Combine(extract, "config.db");
            if (File.Exists(configCopy))
            {
                var (devices, tags, rules) = await CountConfigurationAsync(configCopy, cancellationToken);
                if (devices != manifest.Devices) problems.Add($"配置库设备数不符：清单 {manifest.Devices}，实际 {devices}");
                if (tags != manifest.Tags) problems.Add($"配置库测点数不符：清单 {manifest.Tags}，实际 {tags}");
                if (rules != manifest.AlarmRules) problems.Add($"配置库报警规则数不符：清单 {manifest.AlarmRules}，实际 {rules}");
            }
            else if (manifest.Files.Any(x => x.Path == "config.db"))
            {
                problems.Add("缺少 config.db");
            }

            return new BackupVerification(problems.Count == 0, problems, manifest);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "校验备份 {File} 失败", fileName);
            return new BackupVerification(false, [$"校验异常：{ex.Message}"], null);
        }
        finally
        {
            try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch { }
        }
    }

    public string Resolve(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name) || name != fileName || !name.StartsWith("full_", StringComparison.Ordinal) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("备份文件名无效", nameof(fileName));
        }

        var path = Path.Combine(Root, name);
        return File.Exists(path) ? path : throw new FileNotFoundException("备份不存在", name);
    }

    // 还原到空目录，供演练或在停机窗口内替换现场数据。先校验整包，校验不过一律拒绝。
    // 不通过 HTTP 暴露：运行中覆盖正在使用的库会同时破坏写入与缓存。
    public async Task<RestoreReport> RestoreAsync(string fileName, string targetRoot, CancellationToken cancellationToken)
    {
        var verification = await VerifyAsync(fileName, cancellationToken);
        if (!verification.Valid) throw new InvalidOperationException($"备份校验未通过，拒绝还原：{string.Join("；", verification.Problems)}");

        var target = Path.GetFullPath(targetRoot);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new InvalidOperationException($"目标目录非空，拒绝覆盖：{target}");
        }

        var extract = Path.Combine(Path.GetTempPath(), "scada-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(extract);
            ZipFile.ExtractToDirectory(Resolve(fileName), extract);
            var config = Path.Combine(extract, "config.db");
            if (!File.Exists(config)) throw new InvalidOperationException("备份中缺少 config.db");

            Directory.CreateDirectory(target);
            File.Copy(config, Path.Combine(target, "scada.db"), true);
            CopyTree(Path.Combine(extract, "history"), Path.Combine(target, "history"));
            // Older packages flattened the spool into history/. Move it to the runtime path.
            var legacySpool = Path.Combine(target, "history", "history-spool.ndjson");
            if (File.Exists(legacySpool))
            {
                var spoolTarget = Path.Combine(target, "history", "_spool", "history-spool.ndjson");
                Directory.CreateDirectory(Path.GetDirectoryName(spoolTarget)!);
                File.Move(legacySpool, spoolTarget);
            }
            CopyTree(Path.Combine(extract, "keys"), Path.Combine(target, "keys"));
            CopyTree(Path.Combine(extract, "opc-pki"), Path.Combine(target, "opc-pki"));

            var (devices, tags, rules) = await CountConfigurationAsync(Path.Combine(target, "scada.db"), cancellationToken);
            var files = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count();
            logger.LogWarning("已从备份 {File} 还原到 {Target}，共 {Files} 个文件", fileName, target, files);
            return new RestoreReport(target, files, devices, tags, rules);
        }
        finally
        {
            try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch { }
        }
    }

    private static void CopyTree(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.Now.AddDays(-Math.Max(_settings.BackupRetentionDays, 1));
        foreach (var file in Directory.EnumerateFiles(Root, "full_*.zip"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "删除过期完整备份 {File} 失败", file);
            }
        }
    }

    private async Task<int> CopyHistoryAsync(string staging, List<BackupFile> files, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_settings.HistoryRoot)) return 0;
        var target = Path.Combine(staging, "history");
        Directory.CreateDirectory(target);
        // Capture pending batches before the databases. A concurrent replay may then appear
        // in both copies (idempotent), but cannot disappear between the two snapshots.
        // 打包整个 _spool/（分段 + 遗留单文件），不再假定单一 history-spool.ndjson。
        var spoolDir = Path.Combine(target, "_spool");
        foreach (var copied in await _spool.CopyDirectoryToAsync(spoolDir, cancellationToken))
            files.Add(Describe(staging, copied));
        var count = 0;
        foreach (var source in Directory.EnumerateFiles(_settings.HistoryRoot, "*.db"))
        {
            var copy = Path.Combine(target, Path.GetFileName(source));
            if (!CopyDatabase(source, copy)) continue;
            files.Add(Describe(staging, copy)); count++;
        }

        return count;
    }

    private void CopyDirectory(DirectoryInfo? source, string target, List<BackupFile> files, string staging)
    {
        if (source is null || !source.Exists) return;
        var destination = new DirectoryInfo(target);
        if (!destination.Exists) destination.Create();
        foreach (var file in source.GetFiles())
        {
            var copy = Path.Combine(target, file.Name);
            file.CopyTo(copy, true);
            files.Add(Describe(staging, copy));
        }

        foreach (var directory in source.GetDirectories())
        {
            CopyDirectory(directory, Path.Combine(target, directory.Name), files, staging);
        }
    }

    private static BackupFile Describe(string staging, string fullPath) =>
        new(Path.GetRelativePath(staging, fullPath).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(fullPath).Length, Hash(fullPath));

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool CopyDatabase(string source, string target)
    {
        if (!File.Exists(source)) return false;
        using var from = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False");
        using var to = new SqliteConnection($"Data Source={target};Pooling=False");
        from.Open(); to.Open();
        from.BackupDatabase(to);
        return true;
    }

    private static async Task<(int Devices, int Tags, int Rules)> CountConfigurationAsync(string path, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseSqlite($"Data Source={path};Mode=ReadOnly;Pooling=False").Options;
        await using var db = new ConfigDbContext(options);
        return (await db.Devices.CountAsync(cancellationToken), await db.Tags.CountAsync(cancellationToken), await db.AlarmRules.CountAsync(cancellationToken));
    }

    private static string AppVersion() =>
        typeof(BackupService).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
