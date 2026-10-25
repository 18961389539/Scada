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
    // 归档报表（班报/日报/月报）固定表头的语言；归档文件生成后不可修改，因此按系统配置而非查看人语言。
    public string ReportLocale { get; set; } = "zh-CN";
    // 归档文件保留天数；0 表示永久保留（默认）。到期只删文件，记录保留，并写审计。
    public int ReportArchiveRetentionDays { get; set; }
    // 服务停机后启动时，每个定时模板最多自动回补的已结束周期数（含最近一个）；1 表示不回补更早周期。
    public int ReportBackfillMaxPeriods { get; set; } = 3;
    // 关闭后台定时（测试或只读副本用）；手动生成不受影响。
    public bool ReportSchedulerEnabled { get; set; } = true;
    // 报表归档数字签名（PKCS#7/CMS 分离签名）。证书为 PFX；为空时使用 keys/report-signing.pfx，不存在则自动生成自签名证书（仅用于防篡改，
    // 对外具有法律效力的签名请配置 CA 颁发的证书）。
    public bool ReportSigningEnabled { get; set; } = true;
    public string? ReportSigningCertPath { get; set; }
    public string? ReportSigningCertPassword { get; set; }
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
    // 完整备份保留：至少保留最近 N 份；超过 BackupRetentionDays 天且不在最近 N 份内的删除。异地目标按各自的 KeepLatest 保留。
    public int BackupKeepLatest { get; set; } = 7;
    // 每次定时/手动“备份并演练”后，把备份还原到临时目录并做完整性与数量核对。
    public bool BackupDrillEnabled { get; set; } = true;
    public List<BackupTargetOptions> BackupTargets { get; set; } = [];
    // Prometheus 抓取令牌（Authorization: Bearer）。未配置时 /metrics 只接受有系统权限的登录令牌。
    public string? MetricsToken { get; set; }
    // 日志：目录（默认程序目录下 logs）、保留文件数、单文件上限（MB，超过即滚动）。
    public string? LogRoot { get; set; }
    public int LogRetainedFiles { get; set; } = 30;
    public int LogFileSizeLimitMb { get; set; } = 50;
    // 看门狗：关键循环停滞超过该秒数即记录并退出进程，由服务管理器（SCM/systemd）重启。0 关闭。
    public int WatchdogStallSeconds { get; set; } = 180;
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
    // 报警统计分析：预聚合（小时/日）后台任务周期、首次回补天数、每轮重算最近小时数（吸收迟到的确认/恢复）、保留期；
    // 原始明细分析（抖动/洪水/关联等）最长覆盖天数，超过时只对区间最后这么多天做明细分析，计数类指标用预聚合。
    public bool AlarmRollupEnabled { get; set; } = true;
    public int AlarmRollupIntervalSec { get; set; } = 300;
    public int AlarmRollupBackfillDays { get; set; } = 90;
    public int AlarmRollupRecomputeHours { get; set; } = 48;
    public int AlarmRollupHourlyRetentionDays { get; set; } = 90;
    public int AlarmRollupDailyRetentionDays { get; set; } = 1095;
    public int AlarmAnalyticsRawMaxDays { get; set; } = 31;
    public int AlarmAnalyticsMaxRows { get; set; } = 500_000;
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
    // 未配置控制策略的测点是否也要求操作确认（接口须带 confirmed=true）。默认 false 兼容旧客户端；现场建议开启。
    public bool ControlRequireConfirmByDefault { get; set; }
    public int ConfigRefreshSeconds { get; set; } = 10;
    public bool SeedDemoData { get; set; }
    public bool AggregationEnabled { get; set; } = true;
}

public sealed class BackupTargetOptions
{
    public string Name { get; set; } = "";
    // Directory 或 S3（任何 S3 兼容存储：AWS、MinIO、Ceph 等）。
    public string Type { get; set; } = "Directory";
    public string? Path { get; set; }
    public string? Endpoint { get; set; }
    public string? Bucket { get; set; }
    public string? Prefix { get; set; }
    public string Region { get; set; } = "us-east-1";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public int KeepLatest { get; set; } = 14;
}
