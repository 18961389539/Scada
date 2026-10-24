using Scada.Core.Enums;

namespace Scada.Core.Options;

public sealed class ScadaSettings
{
    public string ConfigDbPath { get; set; } = "data/scada.db";
    public string HistoryRoot { get; set; } = "data/history";
    public string BackupRoot { get; set; } = "data/backups";
    public string DiagnosticsRoot { get; set; } = "data/diagnostics";
    public string? OpcPkiRoot { get; set; }
    public int HistoryRetentionMonths { get; set; } = 2;
    public int Agg5MinRetentionDays { get; set; } = 365;
    public int BackupRetentionDays { get; set; } = 30;
    public int ArchiveFlushIntervalMs { get; set; } = 1000;
    public int ArchiveBatchSize { get; set; } = 2000;
    // 运维告警阈值：低于/高于这些值时 /health 转为 degraded 并写日志。
    public double DiskFreeWarnGb { get; set; } = 5;
    public long HistorySpoolWarnBytes { get; set; } = 8L * 1024 * 1024;
    public int BusBacklogWarn { get; set; } = 10_000;
    // 定时全量备份间隔（小时）；0 表示关闭，默认每周一次。
    public int FullBackupIntervalHours { get; set; } = 168;
    // 报警外发：留空则只走站内推送。同测点同等级在冷却期内只发一次，恢复事件不受冷却限制。
    public string? AlarmWebhookUrl { get; set; }
    public AlarmLevel AlarmWebhookMinLevel { get; set; } = AlarmLevel.Warning;
    public int AlarmWebhookCooldownSec { get; set; } = 60;
    public int AlarmWebhookTimeoutMs { get; set; } = 5000;
    public int AlarmWebhookQueueSize { get; set; } = 500;
    public int PushIntervalMs { get; set; } = 500;
    public int CommandTimeoutMs { get; set; } = 5000;
    public int ConfigRefreshSeconds { get; set; } = 10;
    public bool SeedDemoData { get; set; }
    public bool AggregationEnabled { get; set; } = true;
}
