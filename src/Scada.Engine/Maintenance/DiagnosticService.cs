using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.History;

namespace Scada.Engine.Maintenance;

public sealed record DiagnosticBundle(string FileName, string Path, long Bytes, IReadOnlyList<string> Entries);

// 诊断包：现场出问题时一次性取走版本、配置规模、健康指标、数据库自检结果与最近日志。
// 只放计数与状态，不放设备凭据、令牌或任何密文/明文口令。
public sealed class DiagnosticService(
    IOptions<ScadaSettings> options,
    ICollectorControl collector,
    IRealtimeBus bus,
    HistorySpool spool,
    SystemWatch watch,
    IDbContextFactory<ConfigDbContext> factory,
    ILogger<DiagnosticService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const long MaxLogBytes = 9L * 1024 * 1024;
    private const int MaxLogFiles = 3;
    private const int KeepBundles = 20;

    public string Root => Path.GetFullPath(options.Value.DiagnosticsRoot);

    public async Task<DiagnosticBundle> CreateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Root);
        var staging = Path.Combine(Root, ".staging-" + Guid.NewGuid().ToString("N"));
        var entries = new List<string>();
        try
        {
            Directory.CreateDirectory(staging);

            await WriteManifestAsync(staging, cancellationToken); entries.Add("manifest.json");
            await WriteHealthAsync(staging, cancellationToken); entries.Add("health.json");
            await WriteDatabaseCheckAsync(staging, cancellationToken); entries.Add("db-check.json");
            await WriteNotificationsAsync(staging, cancellationToken); entries.Add("notifications.json");
            var logs = CopyLogs(staging); entries.AddRange(logs);

            var fileName = $"diag_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var target = Path.Combine(Root, fileName);
            ZipFile.CreateFromDirectory(staging, target, CompressionLevel.Optimal, includeBaseDirectory: false);
            Prune();
            logger.LogWarning("已生成诊断包 {File}（{Entries} 个文件）", fileName, entries.Count);
            return new DiagnosticBundle(fileName, target, new FileInfo(target).Length, entries);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception ex) { logger.LogWarning(ex, "清理诊断暂存目录失败"); }
        }
    }

    private async Task WriteManifestAsync(string staging, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var counts = await CountsAsync(cancellationToken);
        var manifest = new
        {
            createdAt = DateTimeOffset.UtcNow,
            appVersion = typeof(DiagnosticService).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            machine = Environment.MachineName,
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            uptimeSeconds = Environment.TickCount64 / 1000,
            counts,
            settings = new
            {
                settings.ConfigDbPath,
                settings.HistoryRoot,
                settings.BackupRoot,
                settings.DiagnosticsRoot,
                settings.HistoryRetentionMonths,
                settings.ArchiveBatchSize,
                settings.ArchiveFlushIntervalMs,
                settings.FullBackupIntervalHours,
                settings.BackupRetentionDays,
                settings.DiskFreeWarnGb,
                settings.HistorySpoolWarnBytes,
                settings.HistorySpoolMaxBytes,
                settings.HistorySpoolSegmentBytes,
                settings.HistorySpoolDropOldestOnFull,
                settings.HistorySpoolMinFreeMb,
                settings.BusBacklogWarn
            }
        };
        await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, Json), cancellationToken);
    }

    private async Task WriteHealthAsync(string staging, CancellationToken cancellationToken)
    {
        var statuses = collector.GetStatuses();
        var snapshot = watch.Current;
        var health = new
        {
            status = snapshot.Warnings.Count > 0 || (statuses.Count > 0 && statuses.All(x => !x.Online)) ? "degraded" : "ok",
            warnings = snapshot.Warnings,
            devices = statuses,
            onlineDevices = statuses.Count(x => x.Online),
            totalDevices = statuses.Count,
            busBacklog = bus.Backlog,
            busDropped = bus.DroppedTotal,
            historySpool = new { pendingBytes = spool.PendingBytes, spilledBatches = spool.SpilledBatches, rejectedBatches = spool.DroppedBatches, segmentCount = spool.SegmentCount, corruptLines = spool.CorruptLines },
            disk = new { freeGb = snapshot.FreeGb }
        };
        await File.WriteAllTextAsync(Path.Combine(staging, "health.json"), JsonSerializer.Serialize(health, Json), cancellationToken);
    }

    // 报警通知发件箱：各状态计数与最近的最终失败条目（只含编号、动作、次数和错误摘要，不含通道凭据）。
    private async Task WriteNotificationsAsync(string staging, CancellationToken cancellationToken)
    {
        object content;
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var counts = await db.NotificationOutbox.AsNoTracking().GroupBy(x => x.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync(cancellationToken);
            var failed = await db.NotificationOutbox.AsNoTracking().Where(x => x.Status == Scada.Core.Entities.NotificationOutboxStatus.Failed)
                .OrderByDescending(x => x.Id).Take(50)
                .Select(x => new { x.Id, x.AlarmEventId, x.RouteId, x.ChannelId, x.Action, x.Attempts, x.CreatedTs, x.LastError }).ToListAsync(cancellationToken);
            // 计算任务最近错误（含执行身份失效），状态随落盘周期写入。
            var calcJobs = await db.CalcJobStates.AsNoTracking().Where(x => x.LastError != null)
                .Join(db.CalcJobs.AsNoTracking(), s => s.JobId, j => j.Id, (s, j) => new { j.Id, j.Name, j.Kind, j.RunAsUserName, s.LastError, s.UpdatedTs }).ToListAsync(cancellationToken);
            content = new { counts, recentFailed = failed, calcJobErrors = calcJobs };
        }
        catch (Exception ex)
        {
            content = new { error = ex.Message };
        }
        await File.WriteAllTextAsync(Path.Combine(staging, "notifications.json"), JsonSerializer.Serialize(content, Json), cancellationToken);
    }

    private async Task WriteDatabaseCheckAsync(string staging, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(options.Value.ConfigDbPath);
        var quickCheck = "skipped";
        string? error = null;
        if (File.Exists(path))
        {
            try
            {
                await using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                await db.OpenAsync(cancellationToken);
                await using var command = db.CreateCommand();
                command.CommandText = "PRAGMA quick_check";
                quickCheck = (string?)await command.ExecuteScalarAsync(cancellationToken) ?? "unknown";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        var result = new { path = Path.GetFileName(path), exists = File.Exists(path), quickCheck, error };
        await File.WriteAllTextAsync(Path.Combine(staging, "db-check.json"), JsonSerializer.Serialize(result, Json), cancellationToken);
    }

    private async Task<object> CountsAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return new
        {
            devices = await db.Devices.CountAsync(cancellationToken),
            tags = await db.Tags.CountAsync(cancellationToken),
            alarmRules = await db.AlarmRules.CountAsync(cancellationToken),
            alarmEvents = await db.AlarmEvents.CountAsync(cancellationToken),
            users = await db.Users.CountAsync(cancellationToken),
            screens = await db.ProcessScreens.CountAsync(cancellationToken),
            objectTemplates = await db.ObjectTemplates.CountAsync(cancellationToken),
            gateways = await db.Gateways.CountAsync(cancellationToken)
        };
    }

    private List<string> CopyLogs(string staging)
    {
        var copied = new List<string>();
        var logRoot = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logRoot)) return copied;

        var target = Path.Combine(staging, "logs");
        Directory.CreateDirectory(target);
        long budget = MaxLogBytes;
        foreach (var file in new DirectoryInfo(logRoot).GetFiles("*.txt").OrderByDescending(x => x.LastWriteTimeUtc).Take(MaxLogFiles))
        {
            if (file.Length > budget) break;
            file.CopyTo(Path.Combine(target, file.Name), true);
            budget -= file.Length;
            copied.Add($"logs/{file.Name}");
        }

        return copied;
    }

    private void Prune()
    {
        foreach (var file in new DirectoryInfo(Root).GetFiles("diag_*.zip").OrderByDescending(x => x.LastWriteTimeUtc).Skip(KeepBundles))
        {
            try { file.Delete(); } catch (Exception ex) { logger.LogWarning(ex, "删除旧诊断包 {File} 失败", file.Name); }
        }
    }
}
