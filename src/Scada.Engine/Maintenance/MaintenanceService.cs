using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data.Infrastructure;

namespace Scada.Engine.Maintenance;

public sealed class MaintenanceService : BackgroundService
{
    private readonly ScadaSettings _settings;
    private readonly IHistoryDbFactory _historyDbFactory;
    private readonly BackupService _backup;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(
        IOptions<ScadaSettings> settings,
        IHistoryDbFactory historyDbFactory,
        BackupService backup,
        ILogger<MaintenanceService> logger)
    {
        _settings = settings.Value;
        _historyDbFactory = historyDbFactory;
        _backup = backup;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await BackupConfigAsync(cancellationToken);
            await FullBackupIfDueAsync(cancellationToken);
            CleanupBackups();
            CleanupRawHistory();
            await CleanupAggregatesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "数据库维护任务失败");
        }
    }

    private async Task BackupConfigAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_settings.BackupRoot);
        var sourcePath = Path.GetFullPath(_settings.ConfigDbPath);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var fileName = $"scada_{DateTime.Now:yyyyMMdd_HHmmss}.db";
        var targetPath = Path.Combine(_settings.BackupRoot, fileName);
        await using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
        await using var target = new SqliteConnection($"Data Source={targetPath}");
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
        _logger.LogInformation("配置库备份完成：{Path}", targetPath);
    }

    private async Task FullBackupIfDueAsync(CancellationToken cancellationToken)
    {
        if (_settings.FullBackupIntervalHours <= 0)
        {
            return;
        }

        var latest = _backup.List().FirstOrDefault();
        if (latest is not null && DateTimeOffset.UtcNow - latest.CreatedAt < TimeSpan.FromHours(_settings.FullBackupIntervalHours))
        {
            return;
        }

        var summary = await _backup.CreateAsync(cancellationToken);
        _logger.LogInformation("已生成定时完整备份：{File}（{Bytes} 字节）", summary.FileName, summary.Bytes);
    }

    private void CleanupBackups()
    {
        if (!Directory.Exists(_settings.BackupRoot))
        {
            return;
        }

        var cutoff = DateTime.Now.AddDays(-Math.Max(_settings.BackupRetentionDays, 1));
        foreach (var file in Directory.EnumerateFiles(_settings.BackupRoot, "scada_*.db"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除过期备份 {File} 失败", file);
            }
        }
    }

    private void CleanupRawHistory()
    {
        if (!Directory.Exists(_settings.HistoryRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddMonths(-Math.Max(_settings.HistoryRetentionMonths, 1));
        foreach (var file in Directory.EnumerateFiles(_settings.HistoryRoot, "raw_*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('_');
            if (parts.Length != 2 || parts[1].Length != 6 ||
                !int.TryParse(parts[1][..4], out var year) ||
                !int.TryParse(parts[1][4..], out var month))
            {
                continue;
            }

            if (year < cutoff.Year || (year == cutoff.Year && month < cutoff.Month))
            {
                TryDelete(file);
                TryDelete(file + "-wal");
                TryDelete(file + "-shm");
            }
        }
    }

    private async Task CleanupAggregatesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_settings.HistoryRoot))
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(_settings.Agg5MinRetentionDays, 1)).ToUnixTimeMilliseconds();
        foreach (var file in Directory.EnumerateFiles(_settings.HistoryRoot, "agg_*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('_');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var year))
            {
                continue;
            }

            try
            {
                await using var db = _historyDbFactory.CreateAggregate(year);
                await db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM Agg5Min WHERE Ts < {0}",
                    new object[] { cutoff },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理聚合数据 {File} 失败", file);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除文件 {File} 失败", path);
        }
    }
}
