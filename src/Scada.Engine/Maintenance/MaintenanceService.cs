using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;

namespace Scada.Engine.Maintenance;

public sealed class MaintenanceService : BackgroundService
{
    private readonly ScadaSettings _settings;
    private readonly IHistoryBackend _history;
    private readonly ILogger<MaintenanceService> _logger;
    private readonly INodeRole _role;
    private readonly IDbContextFactory<RuntimeDbContext> _runtimeFactory;
    private readonly IDbContextFactory<ConfigDbContext>? _configFactory;

    public MaintenanceService(
        IOptions<ScadaSettings> settings,
        IHistoryBackend history,
        ILogger<MaintenanceService> logger,
        IDbContextFactory<RuntimeDbContext> runtimeFactory,
        INodeRole? role = null,
        IDbContextFactory<ConfigDbContext>? configFactory = null)
    {
        _settings = settings.Value;
        _history = history;
        _logger = logger;
        _runtimeFactory = runtimeFactory;
        _role = role ?? new StandaloneNodeRole();
        _configFactory = configFactory;
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

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await BackupConfigAsync(cancellationToken);
            CleanupBackups();
            await _history.CleanupRetentionAsync(cancellationToken);
            await CleanupAlarmEventsAsync(cancellationToken);
            await CleanupOperationLogsAsync(cancellationToken);
            await ReclaimSpaceAsync(cancellationToken);
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

        var fileName = $"scada_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db";
        var targetPath = Path.Combine(_settings.BackupRoot, fileName);
        await using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
        await using var target = new SqliteConnection($"Data Source={targetPath}");
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);
        _logger.LogInformation("配置库备份完成：{Path}", targetPath);
    }

    private void CleanupBackups()
    {
        if (!Directory.Exists(_settings.BackupRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(_settings.BackupRetentionDays, 1));
        foreach (var file in Directory.EnumerateFiles(_settings.BackupRoot, "scada_*.db"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
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

    // 报警记录保留：只清理已结束且没有被停机事件引用的旧报警，避免破坏生产追溯。
    private async Task CleanupAlarmEventsAsync(CancellationToken cancellationToken)
    {
        if (_configFactory is null || _settings.AlarmEventRetentionDays <= 0) return;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.AlarmEventRetentionDays).ToUnixTimeMilliseconds();
            await using var db = await _configFactory.CreateDbContextAsync(cancellationToken);
            await using var runtime = await _runtimeFactory.CreateDbContextAsync(cancellationToken);
            var linkedIds = await runtime.DowntimeEvents.AsNoTracking()
                .Where(x => x.AlarmEventId != null)
                .Select(x => x.AlarmEventId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);
            var deleted = await runtime.AlarmEvents
                .Where(x => x.EndTs != null && x.EndTs < cutoff && !linkedIds.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);
            if (deleted > 0) _logger.LogInformation("清理过期报警记录 {Count} 条", deleted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "清理过期报警记录失败");
        }
    }

    // 操作审计保留：0 表示不自动清理。
    private async Task CleanupOperationLogsAsync(CancellationToken cancellationToken)
    {
        if (_settings.OperationLogRetentionDays <= 0) return;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.OperationLogRetentionDays).ToUnixTimeMilliseconds();
            await using var runtime = await _runtimeFactory.CreateDbContextAsync(cancellationToken);
            var deleted = await runtime.OperationLogs
                .Where(x => x.Ts < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
            if (deleted > 0) _logger.LogInformation("清理过期操作审计 {Count} 条", deleted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "清理过期操作审计失败");
        }
    }

    /// <summary>
    /// 回收已删除行的磁盘空间。SQLite 删除数据只是把页挂到 freelist，文件不会自动缩小；
    /// 运行库的报警与操作日志按天清理，长期运行后文件会停在历史峰值大小，拖慢备份、诊断包与恢复演练。
    ///
    /// 新库在建库时已设 auto_vacuum=INCREMENTAL（见 SqlitePragmaInterceptor），这里触发增量回收；
    /// 老库这条 PRAGMA 是 no-op，不报错，但需要人工执行一次 VACUUM 才会切换模式。
    /// </summary>
    private async Task ReclaimSpaceAsync(CancellationToken cancellationToken)
    {
        await ReclaimAsync<RuntimeDbContext>("运行库", _runtimeFactory.CreateDbContextAsync(cancellationToken), cancellationToken);
        if (_configFactory is not null)
            await ReclaimAsync<ConfigDbContext>("配置库", _configFactory.CreateDbContextAsync(cancellationToken), cancellationToken);
    }

    private async Task ReclaimAsync<TContext>(string label, Task<TContext> creating, CancellationToken cancellationToken)
        where TContext : DbContext
    {
        try
        {
            await using var db = await creating;
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);

            var before = FileLengthMb(connection.DataSource);
            // 先 checkpoint 再回收：WAL 里还有未合并的页时，incremental_vacuum 释放的空间
            // 不会立刻反映到主文件大小，看起来像"没回收"。
            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
            await ExecuteAsync(connection, "PRAGMA incremental_vacuum;", cancellationToken);
            var after = FileLengthMb(connection.DataSource);

            if (before - after >= 1)
                _logger.LogInformation("{Label}空间回收：{Before} MB → {After} MB", label, before, after);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 回收是优化动作：锁冲突或库正被占用时跳过本轮即可。
            // 备份与数据清理已经完成，不能因为收不了空间让整个维护任务判为失败。
            _logger.LogWarning(ex, "{Label}空间回收失败，本轮跳过", label);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static long FileLengthMb(string? path) =>
        string.IsNullOrEmpty(path) || path == ":memory:" || !File.Exists(path)
            ? 0
            : new FileInfo(path).Length / 1024 / 1024;
}
