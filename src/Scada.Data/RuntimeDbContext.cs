using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Scada.Core.Entities;
using Scada.Data.Infrastructure;

namespace Scada.Data;

/// <summary>
/// 运行数据：报警流水、通知发件箱、网关收件箱与游标、控制/配方/报表运行、计算任务状态、
/// 追溯与质检记录、停机事件、操作审计，以及 OEE/SPC 采样。
/// 与配置库分开，避免配置保存和运行写入抢同一把 SQLite 写锁，也不进入配置库的整库主备快照。
/// </summary>
public class RuntimeDbContext : DbContext
{
    public RuntimeDbContext(DbContextOptions<RuntimeDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IMigrationsSqlGenerator, ScadaSqliteMigrationsSqlGenerator>();
        optionsBuilder.AddInterceptors(new SqliteSchemaCommandInterceptor());
    }

    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();
    public DbSet<AlarmActivity> AlarmActivities => Set<AlarmActivity>();
    public DbSet<AlarmRollup> AlarmRollups => Set<AlarmRollup>();
    public DbSet<AlarmRollupState> AlarmRollupStates => Set<AlarmRollupState>();
    public DbSet<AlarmNotificationLog> AlarmNotificationLogs => Set<AlarmNotificationLog>();
    public DbSet<NotificationOutbox> NotificationOutbox => Set<NotificationOutbox>();
    public DbSet<GatewayInboxBatch> GatewayInbox => Set<GatewayInboxBatch>();
    public DbSet<LocalControlCommand> LocalControlCommands => Set<LocalControlCommand>();
    public DbSet<GatewayControlCommand> GatewayCommands => Set<GatewayControlCommand>();
    public DbSet<McpIdempotencyRecord> McpIdempotencyRecords => Set<McpIdempotencyRecord>();
    public DbSet<OeeLineSample> OeeLineSamples => Set<OeeLineSample>();
    public DbSet<OeeRollup> OeeRollups => Set<OeeRollup>();
    public DbSet<OeeRollupState> OeeRollupStates => Set<OeeRollupState>();
    public DbSet<SpcSample> SpcSamples => Set<SpcSample>();
    public DbSet<SpcViolation> SpcViolations => Set<SpcViolation>();
    public DbSet<TraceabilityRecord> TraceabilityRecords => Set<TraceabilityRecord>();
    public DbSet<TraceabilityAttempt> TraceabilityAttempts => Set<TraceabilityAttempt>();
    public DbSet<TraceabilityPendingScan> TraceabilityPendingScans => Set<TraceabilityPendingScan>();
    public DbSet<RecipeDownload> RecipeDownloads => Set<RecipeDownload>();
    public DbSet<ReportRun> ReportRuns => Set<ReportRun>();
    public DbSet<ReportApproval> ReportApprovals => Set<ReportApproval>();
    public DbSet<CalcJobState> CalcJobStates => Set<CalcJobState>();
    public DbSet<CalcJobSnapshot> CalcJobSnapshots => Set<CalcJobSnapshot>();
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();
    public DbSet<QualityInspection> QualityInspections => Set<QualityInspection>();
    public DbSet<QualityInspectionRevision> QualityInspectionRevisions => Set<QualityInspectionRevision>();
    public DbSet<DowntimeEvent> DowntimeEvents => Set<DowntimeEvent>();
    public DbSet<ProductionCountCorrection> ProductionCountCorrections => Set<ProductionCountCorrection>();
    public DbSet<ControlSequenceRun> ControlSequenceRuns => Set<ControlSequenceRun>();
    public DbSet<ProductionBatch> ProductionBatches => Set<ProductionBatch>();
    public DbSet<ControlRequest> ControlRequests => Set<ControlRequest>();
    public DbSet<ControlLockout> ControlLockouts => Set<ControlLockout>();
    public DbSet<DeviceControlState> DeviceControlStates => Set<DeviceControlState>();
    public DbSet<MomentaryLease> MomentaryLeases => Set<MomentaryLease>();
    public DbSet<AnnunciatorSilence> AnnunciatorSilences => Set<AnnunciatorSilence>();
    public DbSet<GatewayDataStream> GatewayStreams => Set<GatewayDataStream>();
    public DbSet<GatewayArchiveCursor> GatewayArchiveCursors => Set<GatewayArchiveCursor>();
    public DbSet<GatewayDeviceHealth> GatewayDeviceHealth => Set<GatewayDeviceHealth>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 运行库表多数由配置库迁入；跨库外键无法在 SQLite 上保留，只保留同行键与索引。
        // 配置侧删除模板/产线/任务时由对应服务清理运行行（逐实体映射见 Configurations/Runtime）。
        // 程序集里同时放着 Config 与 Runtime 两组配置类；必须按命名空间过滤，
        // 否则另一个 DbContext 的实体会被一起建进当前模型（凭空多出几十张表）。
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RuntimeDbContext).Assembly,
            type => type.Namespace == "Scada.Data.Configurations.Runtime");
    }
}
