using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Health;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Server.Ha;

namespace Scada.Server.Reports;

/// <summary>
/// Prometheus 文本格式指标的渲染器。
///
/// 从 <c>MetricsEndpoints</c> 拆出来的原因：原来是一个 <c>static RenderAsync(IServiceProvider, ...)</c>，
/// 里面用 <c>GetRequiredService&lt;T&gt;()</c> 反查了 9 个服务。那种写法有三个代价：
/// 依赖关系编译期不可见、单测必须搭一整棵 DI 树、抓到 NullReference 才知道漏注册。
/// 改成构造函数注入后，<see cref="MetricsEndpoints"/> 只负责鉴权与内容类型，渲染逻辑可以独立构造与验证。
///
/// 注意：抓取频率高（Prometheus 默认 15s），<see cref="RenderAsync"/> 必须保持只读；
/// 唯一的 IO 是一次聚合查询与几个文件大小读取。
/// </summary>
public sealed class MetricsReport(
    HaCoordinator ha,
    ICollectorControl collector,
    HistorySpool spool,
    IRealtimeBus bus,
    SystemWatch watch,
    BackupScheduler scheduler,
    BackupService backups,
    IEnumerable<IHealthContributor> contributors,
    // 可选依赖：未注册时指标里就少这几组，而不是让 /metrics 直接 500。
    HistoryQueryService? queries = null,
    LivenessRegistry? liveness = null,
    MigrationTelemetry? migrations = null)
{
    public async Task<string> RenderAsync(ScadaSettings settings, IDbContextFactory<RuntimeDbContext> runtime, CancellationToken ct)
    {
        var w = new Writer();
        var now = ScadaTime.NowMs();
        w.Gauge("scada_up", "服务存活", 1);
        w.Gauge("scada_uptime_seconds", "进程运行秒数", (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds);
        w.Gauge("scada_process_working_set_bytes", "进程工作集", Environment.WorkingSet);

        var status = ha.Status();
        w.Gauge("scada_ha_enabled", "是否启用主备", ha.Enabled ? 1 : 0);
        w.Gauge("scada_ha_primary", "本节点是否为主机（单机恒为 1）", ha.IsActive ? 1 : 0, ("node", status.NodeId));
        w.Gauge("scada_ha_term", "主备任期", status.Term);
        w.Gauge("scada_ha_peer_up", "主备对端是否可达", status.Peer is null ? 0 : 1);
        w.Counter("scada_ha_role_changes_total", "本进程内角色变化次数", status.Generation);
        w.Gauge("scada_ha_replication_age_seconds", "备机距上次成功配置复制的秒数（主机为 0）", ha.IsActive || status.ConfigSyncTs == 0 ? 0 : (now - status.ConfigSyncTs) / 1000.0);
        w.Gauge("scada_ha_history_rows_behind", "备机历史复制落后行数", ha.IsActive ? 0 : status.HistoryRowsBehind);
        w.Gauge("scada_ha_runtime_rows_behind", "备机运行库复制落后行数", ha.IsActive ? 0 : status.RuntimeRowsBehind);

        var devices = collector.GetStatuses();
        w.Help("scada_device_online", "设备在线（1/0）", "gauge");
        foreach (var d in devices) w.Sample("scada_device_online", d.Online ? 1 : 0, Labels(d));
        w.Help("scada_device_cycle_ms", "最近一次采集周期耗时（采集延迟）", "gauge");
        foreach (var d in devices) w.Sample("scada_device_cycle_ms", d.LastCycleMs, Labels(d));
        w.Help("scada_device_poll_age_seconds", "距最近一次成功采集的秒数", "gauge");
        foreach (var d in devices.Where(x => x.LastPollTs > 0)) w.Sample("scada_device_poll_age_seconds", (now - d.LastPollTs) / 1000.0, Labels(d));
        w.Help("scada_device_comm_failures_total", "通信错误累计", "counter");
        foreach (var d in devices) w.Sample("scada_device_comm_failures_total", d.CommFailures, Labels(d));
        w.Help("scada_device_reconnects_total", "重连次数", "counter");
        foreach (var d in devices) w.Sample("scada_device_reconnects_total", d.ReconnectAttempts, Labels(d));

        await using (var db = await runtime.CreateDbContextAsync(ct))
        {
            var active = await db.AlarmEvents.AsNoTracking().Where(x => x.EndTs == null && !x.Simulated).GroupBy(x => x.Level).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            var simulatedActive = await db.AlarmEvents.AsNoTracking().Where(x => x.EndTs == null && x.Simulated).GroupBy(x => x.Level).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            w.Help("scada_alarms_active", "官方活动报警数（按等级，不含演练）", "gauge");
            foreach (var level in Enum.GetValues<AlarmLevel>()) w.Sample("scada_alarms_active", active.FirstOrDefault(x => x.Key == level)?.Count ?? 0, ("level", level.ToString()));
            w.Help("scada_alarms_active_simulated", "演练活动报警数（按等级）", "gauge");
            foreach (var level in Enum.GetValues<AlarmLevel>()) w.Sample("scada_alarms_active_simulated", simulatedActive.FirstOrDefault(x => x.Key == level)?.Count ?? 0, ("level", level.ToString()));
            w.Gauge("scada_alarms_unacknowledged", "官方未确认报警数（不含演练）", await db.AlarmEvents.AsNoTracking().CountAsync(x => x.AckTs == null && !x.Shelved && !x.Simulated, ct));
            w.Gauge("scada_alarms_unacknowledged_simulated", "演练未确认报警数", await db.AlarmEvents.AsNoTracking().CountAsync(x => x.AckTs == null && !x.Shelved && x.Simulated, ct));
            w.Gauge("scada_notification_outbox_pending", "发件箱积压（待发/发送中）", await db.NotificationOutbox.AsNoTracking()
                .CountAsync(x => x.Status == NotificationOutboxStatus.Pending || x.Status == NotificationOutboxStatus.Sending, ct));
            w.Gauge("scada_notification_outbox_failed", "发件箱最终失败条数", await db.NotificationOutbox.AsNoTracking().CountAsync(x => x.Status == NotificationOutboxStatus.Failed, ct));
            w.Gauge("scada_gateway_inbox_pending_batches", "中心网关收件箱待处理批次", await db.GatewayInbox.AsNoTracking().CountAsync(ct));
        }

        w.Gauge("scada_history_spool_pending_bytes", "历史暂存积压字节", spool.PendingBytes);
        w.Gauge("scada_history_spool_segments", "历史暂存分段数", spool.SegmentCount);
        w.Counter("scada_history_spool_spilled_batches_total", "写入暂存的批次", spool.SpilledBatches);
        w.Counter("scada_history_spool_rejected_batches_total", "暂存拒收的批次（丢数）", spool.DroppedBatches);
        w.Counter("scada_history_spool_corrupt_lines_total", "暂存损坏行", spool.CorruptLines);
        w.Gauge("scada_bus_backlog", "实时总线积压", bus.Backlog);
        w.Counter("scada_bus_dropped_total", "实时总线界面订阅丢弃", bus.DroppedTotal);
        w.Counter("scada_bus_history_rejected_total", "实时总线历史订阅拒收", bus.HistoryRejected);
        w.Counter("scada_bus_history_gap_rejected_total", "实时总线历史缺口标记队列溢出", bus.HistoryGapRejected);

        var configPath = Path.GetFullPath(settings.ConfigDbPath);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(configPath)!);
            w.Gauge("scada_disk_free_bytes", "数据盘可用空间", drive.AvailableFreeSpace);
            w.Gauge("scada_disk_total_bytes", "数据盘总空间", drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        w.Gauge("scada_config_db_bytes", "配置库大小", File.Exists(configPath) ? new FileInfo(configPath).Length : 0);
        // 迁移链健康度：迁移越多、单次耗时越长，新装与升级的启动窗口越久。
        // 这几个值是本次启动的快照（gauge），重启后重新记录，不是累计量。
        if (migrations is { Recorded: true })
        {
            w.Gauge("scada_migrations_applied", "本次启动应用的配置库迁移条数", migrations.Applied);
            w.Gauge("scada_migrations_total", "配置库迁移链总条数", migrations.Total);
            w.Gauge("scada_migrations_duration_seconds", "本次启动配置库迁移耗时（秒）", migrations.DurationSeconds);
            w.Gauge("scada_migrations_recorded_timestamp_seconds", "迁移快照记录时间", migrations.RecordedAt.ToUnixTimeSeconds());
        }
        var capacity = watch.Current.History;
        w.Gauge("scada_history_db_bytes", "历史库总大小", (capacity?.RawBytes ?? 0) + (capacity?.AggregateBytes ?? 0));
        if (capacity is not null)
        {
            w.Gauge("scada_history_storage_bytes", "历史目录实际总占用（包含日志与暂存）", capacity.TotalBytes);
            w.Gauge("scada_history_raw_bytes", "原始历史库占用", capacity.RawBytes);
            w.Gauge("scada_history_aggregate_bytes", "聚合历史库占用", capacity.AggregateBytes);
            w.Gauge("scada_history_journal_bytes", "历史库 WAL 与 SHM 占用", capacity.JournalBytes);
            w.Gauge("scada_history_disk_free_bytes", "历史目录所在盘可用空间", capacity.Disk.FreeBytes ?? double.NaN);
            w.Gauge("scada_history_disk_total_bytes", "历史目录所在盘总空间", capacity.Disk.TotalBytes ?? double.NaN);
            w.Gauge("scada_history_capacity_scan_complete", "历史容量扫描是否完整", capacity.ScanComplete ? 1 : 0);
            w.Gauge("scada_history_capacity_checked_timestamp_seconds", "历史容量最后检查时间", capacity.CheckedAt.ToUnixTimeSeconds());
            w.Gauge("scada_history_growth_bytes_per_day", "历史目录近期每日净增长估计", capacity.GrowthBytesPerDay ?? double.NaN);
            w.Gauge("scada_history_days_until_disk_warn", "预计距磁盘告警阈值天数", capacity.DaysUntilDiskWarn ?? double.NaN);
        }
        if (queries is not null)
        {
            w.Gauge("scada_history_queries_active", "正在执行的历史查询数", queries.ActiveQueries);
            w.Gauge("scada_history_queries_limit", "历史查询并发上限", queries.MaxConcurrent);
            w.Counter("scada_history_queries_rejected_total", "历史查询并发拒绝数", queries.RejectedQueries);
            w.Counter("scada_history_queries_timed_out_total", "历史查询超时数", queries.TimedOutQueries);
            w.Counter("scada_history_queries_read_limited_total", "历史查询读取超限数", queries.ReadLimitedQueries);
        }

        var runs = scheduler.Runs();
        var lastOk = runs.FirstOrDefault(x => x.FileName is not null);
        w.Gauge("scada_backup_last_success_timestamp_seconds", "最近一次成功生成备份的时间", lastOk is null ? 0 : lastOk.FinishedAt.ToUnixTimeSeconds());
        w.Gauge("scada_backup_last_drill_ok", "最近一次恢复演练是否通过（无记录为 -1）", runs.FirstOrDefault(x => x.Drill is not null)?.Drill is { } drill ? (drill.Ok ? 1 : 0) : -1);
        w.Gauge("scada_backup_last_offsite_ok", "最近一次异地拷贝是否全部成功（无目标为 -1）", runs.FirstOrDefault() is { Targets.Count: > 0 } last ? (last.Targets.All(x => x.Ok) ? 1 : 0) : -1);
        w.Gauge("scada_backup_local_count", "本地完整备份份数", backups.List().Count);

        // 走贡献者集合：此前只统计了 HA 与备份两处，漏掉通知/计算/工程快照，指标与 /health 的告警数会对不上。
        var contributedWarnings = contributors.SelectMany(x => x.HealthWarnings()).Count();
        w.Gauge("scada_warnings", "当前健康告警条数", watch.Current.Warnings.Count + contributedWarnings);
        if (liveness is not null)
        {
            w.Help("scada_loop_age_seconds", "关键后台循环距上次心跳的秒数（看门狗）", "gauge");
            foreach (var (name, age) in liveness.Ages()) w.Sample("scada_loop_age_seconds", age.TotalSeconds, ("loop", name));
        }
        return w.ToString();
    }

    private static (string, string)[] Labels(CollectorDeviceStatus d) => [("device_id", d.DeviceId.ToString(CultureInfo.InvariantCulture)), ("device", d.Name)];

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        public void Help(string name, string help, string type) => _sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n').Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        public void Gauge(string name, string help, double value, params (string, string)[] labels) { Help(name, help, "gauge"); Sample(name, value, labels); }
        public void Counter(string name, string help, double value) { Help(name, help, "counter"); Sample(name, value); }
        public void Sample(string name, double value, params (string Key, string Value)[] labels)
        {
            _sb.Append(name);
            if (labels.Length > 0) _sb.Append('{').Append(string.Join(',', labels.Select(l => $"{l.Key}=\"{Escape(l.Value)}\""))).Append('}');
            _sb.Append(' ').Append(double.IsFinite(value) ? value.ToString("G17", CultureInfo.InvariantCulture) : "NaN").Append('\n');
        }
        private static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        public override string ToString() => _sb.ToString();
    }
}
