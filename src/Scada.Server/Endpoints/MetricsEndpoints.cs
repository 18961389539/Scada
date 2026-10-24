using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Server.Auth;
using Scada.Server.Ha;
using Scada.Core.Security;

namespace Scada.Server.Endpoints;

// Prometheus 文本格式指标。鉴权：Scada:MetricsToken 作为 Bearer 令牌，或具有系统权限的登录令牌。
public static class MetricsEndpoints
{
    public static void MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", async (HttpContext http, IOptions<ScadaSettings> settings, IServiceProvider services, CancellationToken ct) =>
        {
            if (!await AllowedAsync(http, settings.Value)) return Results.Json(new { message = "需要指标令牌或系统权限" }, statusCode: 401);
            return Results.Text(await RenderAsync(services, settings.Value, ct), "text/plain; version=0.0.4; charset=utf-8");
        }).AllowAnonymous();
    }

    private static async Task<bool> AllowedAsync(HttpContext http, ScadaSettings settings)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(settings.MetricsToken) && header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var given = Encoding.UTF8.GetBytes(header[7..].Trim());
            if (CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(settings.MetricsToken))) return true;
        }
        if (http.User.Identity?.IsAuthenticated != true || http.User.GetUserId() is null) return false;
        return (await http.GetAccessAsync()).HasGlobal(Permission.System);
    }

    public static async Task<string> RenderAsync(IServiceProvider services, ScadaSettings settings, CancellationToken ct)
    {
        var w = new Writer();
        var now = ScadaTime.NowMs();
        w.Gauge("scada_up", "服务存活", 1);
        w.Gauge("scada_uptime_seconds", "进程运行秒数", (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds);
        w.Gauge("scada_process_working_set_bytes", "进程工作集", Environment.WorkingSet);

        var ha = services.GetRequiredService<HaCoordinator>(); var status = ha.Status();
        w.Gauge("scada_ha_enabled", "是否启用主备", ha.Enabled ? 1 : 0);
        w.Gauge("scada_ha_primary", "本节点是否为主机（单机恒为 1）", ha.IsActive ? 1 : 0, ("node", status.NodeId));
        w.Gauge("scada_ha_term", "主备任期", status.Term);
        w.Gauge("scada_ha_peer_up", "主备对端是否可达", status.Peer is null ? 0 : 1);
        w.Counter("scada_ha_role_changes_total", "本进程内角色变化次数", status.Generation);
        w.Gauge("scada_ha_replication_age_seconds", "备机距上次成功配置复制的秒数（主机为 0）", ha.IsActive || status.ConfigSyncTs == 0 ? 0 : (now - status.ConfigSyncTs) / 1000.0);
        w.Gauge("scada_ha_history_rows_behind", "备机历史复制落后行数", ha.IsActive ? 0 : status.HistoryRowsBehind);

        var collector = services.GetRequiredService<ICollectorControl>();
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

        var factory = services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var active = await db.AlarmEvents.AsNoTracking().Where(x => x.EndTs == null).GroupBy(x => x.Level).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            w.Help("scada_alarms_active", "活动报警数（按等级）", "gauge");
            foreach (var level in Enum.GetValues<Scada.Core.Enums.AlarmLevel>()) w.Sample("scada_alarms_active", active.FirstOrDefault(x => x.Key == level)?.Count ?? 0, ("level", level.ToString()));
            w.Gauge("scada_alarms_unacknowledged", "未确认报警数", await db.AlarmEvents.AsNoTracking().CountAsync(x => x.AckTs == null && !x.Shelved, ct));
            w.Gauge("scada_notification_outbox_pending", "发件箱积压（待发/发送中）", await db.NotificationOutbox.AsNoTracking()
                .CountAsync(x => x.Status == NotificationOutboxStatus.Pending || x.Status == NotificationOutboxStatus.Sending, ct));
            w.Gauge("scada_notification_outbox_failed", "发件箱最终失败条数", await db.NotificationOutbox.AsNoTracking().CountAsync(x => x.Status == NotificationOutboxStatus.Failed, ct));
            w.Gauge("scada_gateway_inbox_pending_batches", "中心网关收件箱待处理批次", await db.GatewayInbox.AsNoTracking().CountAsync(ct));
        }

        var spool = services.GetRequiredService<HistorySpool>();
        w.Gauge("scada_history_spool_pending_bytes", "历史暂存积压字节", spool.PendingBytes);
        w.Gauge("scada_history_spool_segments", "历史暂存分段数", spool.SegmentCount);
        w.Counter("scada_history_spool_spilled_batches_total", "写入暂存的批次", spool.SpilledBatches);
        w.Counter("scada_history_spool_rejected_batches_total", "暂存拒收的批次（丢数）", spool.DroppedBatches);
        w.Counter("scada_history_spool_corrupt_lines_total", "暂存损坏行", spool.CorruptLines);
        var bus = services.GetRequiredService<IRealtimeBus>();
        w.Gauge("scada_bus_backlog", "实时总线积压", bus.Backlog);
        w.Counter("scada_bus_dropped_total", "实时总线丢弃", bus.DroppedTotal);

        var configPath = Path.GetFullPath(settings.ConfigDbPath);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(configPath)!);
            w.Gauge("scada_disk_free_bytes", "数据盘可用空间", drive.AvailableFreeSpace);
            w.Gauge("scada_disk_total_bytes", "数据盘总空间", drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        w.Gauge("scada_config_db_bytes", "配置库大小", File.Exists(configPath) ? new FileInfo(configPath).Length : 0);
        w.Gauge("scada_history_db_bytes", "历史库总大小", Directory.Exists(settings.HistoryRoot) ? Directory.EnumerateFiles(settings.HistoryRoot, "*.db").Sum(x => new FileInfo(x).Length) : 0);

        var scheduler = services.GetRequiredService<BackupScheduler>();
        var runs = scheduler.Runs();
        var lastOk = runs.FirstOrDefault(x => x.FileName is not null);
        w.Gauge("scada_backup_last_success_timestamp_seconds", "最近一次成功生成备份的时间", lastOk is null ? 0 : lastOk.FinishedAt.ToUnixTimeSeconds());
        w.Gauge("scada_backup_last_drill_ok", "最近一次恢复演练是否通过（无记录为 -1）", runs.FirstOrDefault(x => x.Drill is not null)?.Drill is { } drill ? (drill.Ok ? 1 : 0) : -1);
        w.Gauge("scada_backup_last_offsite_ok", "最近一次异地拷贝是否全部成功（无目标为 -1）", runs.FirstOrDefault() is { Targets.Count: > 0 } last ? (last.Targets.All(x => x.Ok) ? 1 : 0) : -1);
        w.Gauge("scada_backup_local_count", "本地完整备份份数", services.GetRequiredService<BackupService>().List().Count);

        var watch = services.GetRequiredService<SystemWatch>();
        w.Gauge("scada_warnings", "当前健康告警条数", watch.Current.Warnings.Count + ha.Warnings().Count() + scheduler.Warnings().Count);
        var liveness = services.GetService<LivenessRegistry>();
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
