using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Engine.Notifications;

namespace Scada.Server.Services;

public sealed record HealthHa(string Node, string Role, string? PrimaryUrl);
public sealed record HealthHistoryQueries(int Active, int MaxConcurrent, int TimeoutSeconds, int MaxReadPoints,
    int AnalysisMaxPoints, long Rejected, long TimedOut, long ReadLimited);
public sealed record HealthSpool(long PendingBytes, long SpilledBatches, long RejectedBatches, int SegmentCount, long CorruptLines);
public sealed record HealthWebhook(bool Enabled, long Sent, long Suppressed, long Failed, long Dropped, string? LastError);
public sealed record HealthNotifications(long Sent, long Failed, long Dropped, string? LastError, int Pending, int FinalFailed);
public sealed record HealthDisk(string Root, double TotalGb, double FreeGb);
public sealed record HealthStorage(string ConfigDb, long ConfigDbBytes, long HistoryBytes);

public sealed record HealthSnapshot(
    string Status,
    IReadOnlyList<SystemWarning> Warnings,
    HealthHa? Ha,
    string Environment,
    DateTimeOffset ServerTime,
    long UptimeSeconds,
    long HostUptimeSeconds,
    IReadOnlyList<CollectorDeviceStatus> Devices,
    int OnlineDevices,
    int TotalDevices,
    int ConfiguredTags,
    int BusBacklog,
    long BusDropped,
    HistoryCapacity? HistoryCapacity,
    HealthHistoryQueries? HistoryQueries,
    HealthSpool HistorySpool,
    HealthWebhook AlarmWebhook,
    HealthNotifications? AlarmNotifications,
    HealthDisk Disk,
    HealthStorage Storage);

// Shared by /api/health and the MCP query_health tool so both report the same snapshot.
public sealed class HealthSnapshotService(
    ICollectorControl collector,
    IRealtimeBus bus,
    IConfigSnapshot config,
    HistorySpool spool,
    SystemWatch watch,
    IAlarmWebhookStatus webhook,
    IServiceProvider services,
    IOptions<ScadaSettings> settings,
    IHostEnvironment environment)
{
    public HealthSnapshot Snapshot()
    {
        var statuses = collector.GetStatuses();
        var online = statuses.Count(x => x.Online);
        var configPath = Path.GetFullPath(settings.Value.ConfigDbPath);
        var dataRoot = Path.GetPathRoot(configPath) ?? configPath;

        double totalGb = 0;
        double freeGb = 0;
        try
        {
            var drive = new DriveInfo(dataRoot);
            totalGb = Math.Round(drive.TotalSize / 1024d / 1024d / 1024d, 2);
            freeGb = Math.Round(drive.AvailableFreeSpace / 1024d / 1024d / 1024d, 2);
        }
        catch
        {
            // 拿不到盘符（路径无根/网络盘未就绪）时总容量与剩余空间保持 0，健康接口照常返回，由前端显示为未知。
        }

        var snapshot = watch.Current;
        var capacity = snapshot.History;
        var historyBytes = (capacity?.RawBytes ?? 0) + (capacity?.AggregateBytes ?? 0);
        var queries = services.GetService<HistoryQueryService>();

        var status = statuses.Count > 0 && online == 0 ? "degraded" : "ok";
        var warnings = snapshot.Warnings
            .Concat(webhook.Warnings().Select(message => new SystemWarning("alarm_webhook_failing", message)))
            .Concat((services.GetService<AlarmNotificationDispatcher>()?.Warnings() ?? []).Select(message => new SystemWarning("alarm_notification_failed", message)))
            .Concat((services.GetService<Scada.Engine.Calculations.CalcJobEngine>()?.Warnings() ?? []).Select(message => new SystemWarning("calc_job_identity", message)))
            .Concat(services.GetService<Scada.Server.Ha.HaCoordinator>()?.Warnings() ?? [])
            .Concat(services.GetService<BackupScheduler>()?.Warnings() ?? [])
            .Concat((services.GetService<Scada.Server.Projects.ProjectService>()?.Warnings() ?? []).Select(message => new SystemWarning("config_snapshot_failed", message)))
            .ToList();
        if (warnings.Count > 0) status = "degraded";

        return new HealthSnapshot(
            status,
            warnings,
            services.GetService<Scada.Server.Ha.HaCoordinator>() is { Enabled: true } ha
                ? new HealthHa(ha.NodeId, ha.IsActive ? "Primary" : "Standby", ha.PrimaryUrl)
                : null,
            environment.EnvironmentName,
            DateTimeOffset.UtcNow,
            // 进程运行时长：运维看的是「服务多久没重启」，而不是「这台机器多久没关机」
            ProcessUptimeSeconds(),
            Environment.TickCount64 / 1000,
            statuses,
            online,
            statuses.Count,
            config.Tags.Count,
            bus.Backlog,
            bus.DroppedTotal,
            capacity,
            queries is null ? null : new HealthHistoryQueries(
                queries.ActiveQueries,
                queries.MaxConcurrent,
                Math.Clamp(settings.Value.HistoryQueryTimeoutSeconds, 1, 300),
                Math.Max(1, settings.Value.HistoryQueryMaxReadPoints),
                Math.Max(1, settings.Value.HistoryAnalysisMaxPoints),
                queries.RejectedQueries,
                queries.TimedOutQueries,
                queries.ReadLimitedQueries),
            new HealthSpool(spool.PendingBytes, spool.SpilledBatches, spool.DroppedBatches, spool.SegmentCount, spool.CorruptLines),
            new HealthWebhook(webhook.Enabled, webhook.Sent, webhook.Suppressed, webhook.Failed, webhook.Dropped, webhook.LastError),
            services.GetService<AlarmNotificationDispatcher>() is { } dispatcher
                ? new HealthNotifications(dispatcher.Sent, dispatcher.Failed, dispatcher.Dropped, dispatcher.LastError, dispatcher.Pending, dispatcher.FinalFailed)
                : null,
            new HealthDisk(dataRoot, totalGb, freeGb),
            new HealthStorage(Path.GetFileName(configPath), File.Exists(configPath) ? new FileInfo(configPath).Length : 0, historyBytes));
    }

    /// <summary>
    /// 进程运行秒数。原先这里用的是 <c>Environment.TickCount64</c>，那是**系统开机时长**，
    /// 服务重启后不会归零，运维据此判断「服务多久没重启」会得到错误结论。取不到进程启动时间时返回 0（界面显示 0 秒）。
    /// </summary>
    private static long ProcessUptimeSeconds()
    {
        try
        {
            var start = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            return Math.Max(0, (long)(DateTime.Now - start).TotalSeconds);
        }
        catch
        {
            return 0;
        }
    }
}
