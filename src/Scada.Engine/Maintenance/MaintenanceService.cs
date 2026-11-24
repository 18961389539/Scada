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
    private readonly SpaceReclaimService? _reclaim;

    public MaintenanceService(
        IOptions<ScadaSettings> settings,
        IHistoryBackend history,
        ILogger<MaintenanceService> logger,
        IDbContextFactory<RuntimeDbContext> runtimeFactory,
        INodeRole? role = null,
        IDbContextFactory<ConfigDbContext>? configFactory = null,
        SpaceReclaimService? reclaim = null)
    {
        _settings = settings.Value;
        _history = history;
        _logger = logger;
        _runtimeFactory = runtimeFactory;
        _role = role ?? new StandaloneNodeRole();
        _configFactory = configFactory;
        _reclaim = reclaim;
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
    /// Reclaim free pages via SpaceReclaimService: incremental_vacuum when already incremental;
    /// time-limited exclusive VACUUM for legacy none-mode files when history queries are idle.
    /// </summary>
    private async Task ReclaimSpaceAsync(CancellationToken cancellationToken)
    {
        if (_reclaim is null)
        {
            _logger.LogDebug("未注册空间回收服务，本轮跳过");
            return;
        }
        var runs = await _reclaim.ReclaimAsync(cancellationToken, allowVacuum: true);
        foreach (var run in runs)
        {
            if (run.Action == SpaceReclaimService.ActionSkipped)
                _logger.LogInformation("空间回收跳过 {Target}：{Reason}", run.TargetId, run.Reason);
            else if (!run.Ok)
                _logger.LogWarning("空间回收失败 {Target}：{Reason}", run.TargetId, run.Reason);
            else if (run.BeforeBytes - run.AfterBytes >= 1024 * 1024)
                _logger.LogInformation("空间回收 {Target}：{Before} → {After}（{Action}）",
                    run.TargetId, run.BeforeBytes, run.AfterBytes, run.Action);
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
