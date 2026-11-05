using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;
using Scada.Data.Infrastructure;

namespace Scada.Engine.Maintenance;

public sealed class MaintenanceService : BackgroundService
{
    private readonly ScadaSettings _settings;
    private readonly IHistoryDbFactory _historyDbFactory;
    private readonly ILogger<MaintenanceService> _logger;
    private readonly INodeRole _role;
    private readonly IDbContextFactory<RuntimeDbContext> _runtimeFactory;
    private readonly IDbContextFactory<ConfigDbContext>? _configFactory;

    public MaintenanceService(
        IOptions<ScadaSettings> settings,
        IHistoryDbFactory historyDbFactory,
        ILogger<MaintenanceService> logger,
        IDbContextFactory<RuntimeDbContext> runtimeFactory,
        INodeRole? role = null,
        IDbContextFactory<ConfigDbContext>? configFactory = null)
    {
        _settings = settings.Value;
        _historyDbFactory = historyDbFactory;
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
            CleanupRawHistory();
            await CleanupAggregatesAsync(cancellationToken);
            await CleanupAlarmEventsAsync(cancellationToken);
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
                if (_settings.AggregationEnabled && _role.IsActive)
                {
                    using var db = _historyDbFactory.CreateRaw(year, month);
                    if (db.PendingAggregations.Any(x => x.WindowMs == PendingAggregation.FiveMinuteMs))
                    {
                        _logger.LogWarning("原始历史库 {File} 尚有待聚合窗口，暂缓保留清理", file);
                        continue;
                    }
                }
                _historyDbFactory.ReleaseFile(file);
                if (TryDelete(file))
                {
                    TryDelete(file + "-wal");
                    TryDelete(file + "-shm");
                }
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
        cutoff -= cutoff % PendingAggregation.HourMs;
        var hourCutoff = _settings.AggHourRetentionDays > 0
            ? DateTimeOffset.UtcNow.AddDays(-_settings.AggHourRetentionDays).ToUnixTimeMilliseconds() : (long?)null;
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
                    """
                    DELETE FROM Agg5Min WHERE Ts < {0} AND
                    ({1} = 0 OR NOT EXISTS (
                        SELECT 1 FROM PendingAggregation p
                        WHERE p.WindowMs = 3600000 AND p.Ts = Agg5Min.Ts - (((Agg5Min.Ts - IFNULL((SELECT OffsetMs FROM AggregationOffset WHERE Id = 1), 0)) % 3600000 + 3600000) % 3600000)))
                    """,
                    new object[] { cutoff, _settings.AggregationEnabled && _role.IsActive ? 1 : 0 },
                    cancellationToken);
                if (hourCutoff is { } hour)
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM AggHour WHERE Ts < {0}", new object[] { hour }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "清理聚合数据 {File} 失败", file);
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
            var linkedIds = await db.DowntimeEvents.AsNoTracking()
                .Where(x => x.AlarmEventId != null)
                .Select(x => x.AlarmEventId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);
            await using var runtime = await _runtimeFactory.CreateDbContextAsync(cancellationToken);
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

    private bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除文件 {File} 失败", path);
            return false;
        }
    }
}
