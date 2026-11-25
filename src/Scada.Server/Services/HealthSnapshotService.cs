using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.History;
using Scada.Engine.Health;
using Scada.Engine.Maintenance;
using Scada.Engine.Notifications;
using Scada.Server.Ha;

namespace Scada.Server.Services;

public sealed record HealthHa(string Node, string Role, string? PrimaryUrl);
public sealed record HealthHistoryQueries(int Active, int MaxConcurrent, int TimeoutSeconds, int MaxReadPoints,
    int AnalysisMaxPoints, long Rejected, long TimedOut, long ReadLimited);
public sealed record HealthSpool(long PendingBytes, long SpilledBatches, long RejectedBatches, int SegmentCount, long CorruptLines);
public sealed record HealthWebhook(bool Enabled, long Sent, long Suppressed, long Failed, long Dropped, string? LastError);
public sealed record HealthNotifications(long Sent, long Failed, long Dropped, string? LastError, int Pending, int FinalFailed);
public sealed record HealthDisk(string Root, double TotalGb, double FreeGb);
public sealed record HealthStorage(string ConfigDb, long ConfigDbBytes, long HistoryBytes);
public sealed record HealthHistoryLoss(bool Persistent, int Windows, long? OldestFrom, string Note);
public sealed record HealthSimulationDevice(int Id, string Name);
public sealed record HealthTakeoverRecovery(bool Active, long PromotedTs, string Message);

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
    long BusHistoryRejected,
    long BusHistoryGapRejected,
    HistoryCapacity? HistoryCapacity,
    HealthHistoryQueries? HistoryQueries,
    HealthSpool HistorySpool,
    HealthWebhook AlarmWebhook,
    HealthNotifications? AlarmNotifications,
    HealthDisk Disk,
    HealthStorage Storage,
    SpaceReclaimStatus? SpaceReclaim = null,
    HealthHistoryLoss? HistoryLoss = null,
    HealthTakeoverReview? TakeoverReview = null,
    IReadOnlyList<HealthSimulationDevice>? SimulationDevices = null,
    HealthTakeoverRecovery? TakeoverRecovery = null);

// Shared by /api/health and the MCP query_health tool so both report the same snapshot.
// 依赖通过 IHealthContributor 集合收集，不再用 IServiceProvider 反查具体服务类型：
// 反查会把依赖关系藏进方法体，改构造函数只能等运行时才发现；集合注入让编译器与 DI 都能提前发现断链。
// 集合负责「有哪些告警」；另外三个具体依赖负责结构化字段（HA 角色、通知计数、历史查询计数），
// 它们必须是编译期可见的强依赖，而不是运行期字符串查找。
public sealed class HealthSnapshotService(
    ICollectorControl collector,
    IRealtimeBus bus,
    IConfigSnapshot config,
    HistorySpool spool,
    SystemWatch watch,
    IAlarmWebhookStatus webhook,
    Scada.Server.Ha.HaCoordinator ha,
    AlarmNotificationDispatcher dispatcher,
    IEnumerable<IHealthContributor> contributors,
    IOptions<ScadaSettings> settings,
    IHostEnvironment environment,
    // 可选依赖放最后：C# 要求带默认值的参数在所有必填参数之后。
    // 历史查询统计只在 Engine 注册后才存在（轻量宿主与单测可能没有）。
    HistoryQueryService? queries = null,
    SpaceReclaimService? spaceReclaim = null,
    HistoryLossLog? historyLoss = null,
    Scada.Server.Ha.TakeoverReviewService? takeoverReview = null)
{
    private readonly object _cacheGate = new();
    private HealthSnapshot? _cached;
    private long _cachedAt;
    private string _cachedWarnings = "";

    // 前端在报警/实时事件后（600ms 合批）就会调 /health，多个客户端同时在线时这不再是"偶尔一次"：
    // 每次 Snapshot 要构造 DriveInfo（慢盘与网络盘上 AvailableFreeSpace 可达几十毫秒）、
    // 读 ConfigDb 文件长度、遍历全部设备状态。1.5 秒 TTL 让同一波事件只算一次。
    // 但**告警不能等 TTL**：运维刚补上失败的配置快照就查 /health，缓存里还留着
    // "config_snapshot_failed"，会让人以为修复没生效；新出现的故障也会晚 1.5 秒可见。
    // 所以告警集合参与缓存键，只有「告警没变且未超 TTL」才复用缓存。
    private const int CacheTtlMs = 1_500;

    public HealthSnapshot Snapshot()
    {
        var now = Environment.TickCount64;
        var warnings = CurrentWarnings();
        var stamp = string.Join("\n", warnings);
        lock (_cacheGate)
        {
            if (_cached is not null && now - _cachedAt < CacheTtlMs && _cachedWarnings == stamp) return _cached;
        }

        // 构建放在锁外：并发未命中时可能各算一次，但每次都很快，且不会因为一个慢 DriveInfo
        // 把所有读取者堵在锁上。最后写入的快照同样有效，下一次读取直接命中。
        var built = BuildSnapshot(warnings);
        lock (_cacheGate)
        {
            _cached = built;
            _cachedAt = Environment.TickCount64;
            _cachedWarnings = stamp;
        }
        return built;
    }

    // 三个来源都是内存态，取值很便宜；真正慢的是磁盘与设备状态枚举，留在 BuildSnapshot 里。
    private List<SystemWarning> CurrentWarnings() => watch.Current.Warnings
        .Concat(webhook.Warnings().Select(message => new SystemWarning("alarm_webhook_failing", message)))
        .Concat(contributors.SelectMany(contributor => contributor.HealthWarnings()))
        .ToList();

    private HealthSnapshot BuildSnapshot(IReadOnlyList<SystemWarning> warnings)
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

        var status = statuses.Count > 0 && online == 0 ? "degraded" : "ok";
        if (warnings.Count > 0) status = "degraded";

        return new HealthSnapshot(
            status,
            warnings,
            ha is { Enabled: true } ? new HealthHa(ha.NodeId, ha.IsActive ? "Primary" : "Standby", ha.PrimaryUrl) : null,
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
            bus.HistoryRejected,
            bus.HistoryGapRejected,
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
            new HealthNotifications(dispatcher.Sent, dispatcher.Failed, dispatcher.Dropped, dispatcher.LastError, dispatcher.Pending, dispatcher.FinalFailed),
            new HealthDisk(dataRoot, totalGb, freeGb),
            new HealthStorage(Path.GetFileName(configPath), File.Exists(configPath) ? new FileInfo(configPath).Length : 0, historyBytes),
            spaceReclaim?.Snapshot(),
            MapHistoryLoss(historyLoss),
            takeoverReview is null ? null : takeoverReview.SnapshotAsync().GetAwaiter().GetResult(),
            SimulationDevices(),
            TakeoverRecovery(statuses));
    }

    private IReadOnlyList<HealthSimulationDevice> SimulationDevices() =>
        config.Devices.Where(device => device.Enabled && device.SimulationEnabled)
            .Select(device => new HealthSimulationDevice(device.Id, device.Name))
            .ToList();

    private HealthTakeoverRecovery? TakeoverRecovery(IReadOnlyList<CollectorDeviceStatus> statuses)
    {
        if (!ha.Enabled || !ha.IsActive || ha.PromotedTs <= 0) return null;
        var age = ScadaTime.NowMs() - ha.PromotedTs;
        var starting = statuses.Any(status =>
            !status.Online && (status.Error == "等待连接"
                || (status.Error is not null && status.Error.StartsWith("旧采集 I/O"))
                || (status.Error is not null && status.Error.Contains("正在重新启动采集"))));
        if (age > 120_000 && !starting) return null;
        return new HealthTakeoverRecovery(true, ha.PromotedTs, "本机刚接管，采集正在恢复");
    }

    /// <summary>
    /// 进程运行秒数。原先这里用的是 <c>Environment.TickCount64</c>，那是**系统开机时长**，
    /// 服务重启后不会归零，运维据此判断「服务多久没重启」会得到错误结论。取不到进程启动时间时返回 0（界面显示 0 秒）。
    /// </summary>
    private static HealthHistoryLoss? MapHistoryLoss(HistoryLossLog? log)
    {
        if (log is null) return null;
        var status = log.Status();
        return new HealthHistoryLoss(status.Persistent, status.Windows, status.OldestFrom, status.Note);
    }

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
