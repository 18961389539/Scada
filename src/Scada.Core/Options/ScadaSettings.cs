using Scada.Core.Enums;

namespace Scada.Core.Options;

public sealed class ScadaSettings
{
    public string ConfigDbPath { get; set; } = "data/scada.db";
    public string HistoryRoot { get; set; } = "data/history";
    public string BackupRoot { get; set; } = "data/backups";
    public string DiagnosticsRoot { get; set; } = "data/diagnostics";
    // 已生成报表归档目录（只追加，文件带 SHA256 校验）。
    public string ReportsRoot { get; set; } = "data/reports";
    // PDF 中文字体文件（.ttf/.otf/.ttc）；为空时依次尝试常见系统字体。
    public string? ReportFontPath { get; set; }
    public string? OpcPkiRoot { get; set; }
    public int HistoryRetentionMonths { get; set; } = 2;
    public int Agg5MinRetentionDays { get; set; } = 365;
    public int BackupRetentionDays { get; set; } = 30;
    public int ArchiveFlushIntervalMs { get; set; } = 1000;
    public int ArchiveBatchSize { get; set; } = 2000;
    // 运维告警阈值：低于/高于这些值时 /health 转为 degraded 并写日志。
    public double DiskFreeWarnGb { get; set; } = 5;
    public long HistorySpoolWarnBytes { get; set; } = 8L * 1024 * 1024;
    // 历史暂存总容量与单段大小；满盘默认拒收，DropOldestOnFull=true 时删最旧完整段再写。
    public long HistorySpoolMaxBytes { get; set; } = 16L * 1024 * 1024;
    public long HistorySpoolSegmentBytes { get; set; } = 4L * 1024 * 1024;
    public bool HistorySpoolDropOldestOnFull { get; set; }
    // 暂存拒收门槛（MB）：可用空间低于此值才拒绝落盘；DiskFreeWarnGb 只负责告警。0 表示不检查。
    public long HistorySpoolMinFreeMb { get; set; } = 512;
    public int BusBacklogWarn { get; set; } = 10_000;
    // 定时全量备份间隔（小时）；0 表示关闭，默认每周一次。
    public int FullBackupIntervalHours { get; set; } = 168;
    // 报警外发：留空则只走站内推送。同测点同等级在冷却期内只发一次，恢复事件不受冷却限制。
    public string? AlarmWebhookUrl { get; set; }
    public AlarmLevel AlarmWebhookMinLevel { get; set; } = AlarmLevel.Warning;
    public int AlarmWebhookCooldownSec { get; set; } = 60;
    public int AlarmWebhookTimeoutMs { get; set; } = 5000;
    public int AlarmWebhookQueueSize { get; set; } = 500;
    // 报警管理（ISA-18.2）：暂挂上限、抖动判定、洪水阈值（每 10 分钟）、长期驻留、升级扫描周期、班次时区。
    public int AlarmShelveMaxMinutes { get; set; } = 480;
    public int AlarmChatterCount { get; set; } = 3;
    public int AlarmChatterWindowSec { get; set; } = 60;
    public int AlarmFloodThreshold { get; set; } = 10;
    public int AlarmStandingHours { get; set; } = 24;
    public int AlarmEscalationScanSec { get; set; } = 15;
    public string? ShiftTimeZone { get; set; }
    // 计算任务：运行状态落盘周期（断电最多丢失该时长的累计量）与脚本执行上限。
    public int CalcStatePersistSec { get; set; } = 10;
    // 发件箱中已发送/最终失败记录的保留天数。
    public int NotificationOutboxRetentionDays { get; set; } = 30;
    public int CalcScriptTimeoutMs { get; set; } = 50;
    public long CalcScriptMaxSteps { get; set; } = 100_000;
    // 通知发件箱：最多尝试次数、首次重试间隔（之后指数退避）与上限。
    public int NotificationMaxAttempts { get; set; } = 5;
    public int NotificationRetryBaseSec { get; set; } = 30;
    public int NotificationRetryMaxSec { get; set; } = 1800;
    public int PushIntervalMs { get; set; } = 500;
    public int CommandTimeoutMs { get; set; } = 5000;
    public int ConfigRefreshSeconds { get; set; } = 10;
    public bool SeedDemoData { get; set; }
    public bool AggregationEnabled { get; set; } = true;
}
