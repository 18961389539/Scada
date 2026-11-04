using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Scada.Core.Options;

namespace Scada.Data;

public static partial class DatabaseInitializer
{
    /// <summary>迁移前备份的保留份数。</summary>
    public const int PreMigrationBackupCount = 5;

    /// <summary>
    /// 迁移前把配置库原样拷一份。SQLite 的单条迁移在事务里，但进程被杀、磁盘写满或后续迁移逻辑
    /// 出错时，现场库仍会停在“迁移到一半”的状态；有了这份拷贝可以整库还原，
    /// 不必从备份策略里反推哪一份对得上当前版本。
    /// </summary>
    /// <returns>备份文件路径；没有待应用迁移、或配置库尚不存在时返回 null。</returns>
    public static string? BackupBeforeMigration(
        ConfigDbContext db,
        ScadaSettings settings,
        string configPath,
        IReadOnlyList<string> pending,
        ILogger? logger)
    {
        if (pending.Count == 0 || !File.Exists(configPath)) return null;

        var directory = string.IsNullOrWhiteSpace(settings.BackupRoot)
            ? Path.Combine(Path.GetDirectoryName(configPath)!, "backups")
            : Path.GetFullPath(settings.BackupRoot);
        Directory.CreateDirectory(directory);

        // WAL 里可能还有未回写的数据：先 checkpoint 再拷主文件，否则备份会缺最后一段写入。
        try
        {
            db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "迁移前 WAL checkpoint 失败，改为直接拷贝主库文件");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(directory, $"pre-migrate-{stamp}.db");
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(directory, $"pre-migrate-{stamp}-{i}.db");
        }

        try
        {
            File.Copy(configPath, target, overwrite: false);
        }
        catch (Exception ex)
        {
            // 备份失败就不迁移：带着“没有回滚点”的状态升级，比直接不启动更危险。
            throw new InvalidOperationException($"迁移前备份配置库失败，已中止启动：{ex.Message}", ex);
        }

        Prune(directory, logger);
        logger?.LogInformation("配置库待应用迁移 {Count} 条，已备份到 {Path}", pending.Count, target);
        return target;
    }

    private static void Prune(string directory, ILogger? logger)
    {
        try
        {
            var stale = new DirectoryInfo(directory)
                .GetFiles("pre-migrate-*.db")
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .Skip(PreMigrationBackupCount)
                .ToArray();
            foreach (var file in stale) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响本次启动：多留几份备份比启动失败划算。
            logger?.LogWarning(ex, "清理旧的迁移前备份失败：{Directory}", directory);
        }
    }
}
