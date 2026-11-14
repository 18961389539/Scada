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
    public DbSet<RecipeDownload> RecipeDownloads => Set<RecipeDownload>();
    public DbSet<ReportRun> ReportRuns => Set<ReportRun>();
    public DbSet<ReportApproval> ReportApprovals => Set<ReportApproval>();
    public DbSet<CalcJobState> CalcJobStates => Set<CalcJobState>();
    public DbSet<CalcJobSnapshot> CalcJobSnapshots => Set<CalcJobSnapshot>();
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();
    public DbSet<QualityInspection> QualityInspections => Set<QualityInspection>();
    public DbSet<DowntimeEvent> DowntimeEvents => Set<DowntimeEvent>();
    public DbSet<ProductionCountCorrection> ProductionCountCorrections => Set<ProductionCountCorrection>();
    public DbSet<ControlSequenceRun> ControlSequenceRuns => Set<ControlSequenceRun>();
    public DbSet<ControlRequest> ControlRequests => Set<ControlRequest>();
    public DbSet<ControlLockout> ControlLockouts => Set<ControlLockout>();
    public DbSet<DeviceControlState> DeviceControlStates => Set<DeviceControlState>();
    public DbSet<AnnunciatorSilence> AnnunciatorSilences => Set<AnnunciatorSilence>();
    public DbSet<GatewayDataStream> GatewayStreams => Set<GatewayDataStream>();
    public DbSet<GatewayArchiveCursor> GatewayArchiveCursors => Set<GatewayArchiveCursor>();
    public DbSet<GatewayDeviceHealth> GatewayDeviceHealth => Set<GatewayDeviceHealth>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AlarmEvent>(entity =>
        {
            entity.ToTable("AlarmEvent");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Message).IsRequired().HasMaxLength(500);
            entity.Property(x => x.MessageEn).HasMaxLength(500);
            entity.Property(x => x.AckUser).HasMaxLength(100);
            entity.Property(x => x.EndReason).HasMaxLength(100);
            entity.Property(x => x.SuppressReason).HasMaxLength(200);
            entity.HasIndex(x => x.RuleId);
            entity.HasIndex(x => new { x.DeviceId, x.GatewayId }).IsUnique()
                .HasFilter("\"GatewayId\" IS NOT NULL AND \"DeviceId\" IS NOT NULL AND \"RuleId\" IS NULL AND \"TagId\" IS NULL AND \"EndTs\" IS NULL");
            entity.HasIndex(x => x.StartTs);
            entity.HasIndex(x => new { x.Category, x.StartTs });
            entity.HasIndex(x => new { x.TagId, x.StartTs });
            entity.HasIndex(x => new { x.RuleId, x.StartTs });
            entity.HasIndex(x => new { x.EndTs, x.StartTs });
            entity.HasIndex(x => x.EndTs);
        });
        modelBuilder.Entity<AlarmActivity>(entity =>
        {
            entity.ToTable("AlarmActivity");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).IsRequired().HasMaxLength(40);
            entity.Property(x => x.User).HasMaxLength(100);
            entity.Property(x => x.Note).HasMaxLength(1000);
            entity.HasIndex(x => new { x.AlarmEventId, x.Ts });
            entity.HasOne<AlarmEvent>().WithMany().HasForeignKey(x => x.AlarmEventId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AlarmRollup>(entity =>
        {
            entity.ToTable("AlarmRollup");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Source).IsRequired().HasMaxLength(120);
            entity.Property(x => x.Message).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Category).HasDefaultValue(Scada.Core.Enums.AlarmCategory.Process);
            entity.HasIndex(x => new { x.Granularity, x.BucketTs, x.Source, x.Level }).IsUnique();
        });
        modelBuilder.Entity<AlarmRollupState>(entity =>
        {
            entity.ToTable("AlarmRollupState");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.LastError).HasMaxLength(500);
        });
        modelBuilder.Entity<AlarmNotificationLog>(entity =>
        {
            entity.ToTable("AlarmNotificationLog");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).IsRequired().HasMaxLength(40);
            entity.Property(x => x.Error).HasMaxLength(500);
            entity.HasIndex(x => new { x.AlarmEventId, x.RouteId, x.Action });
            entity.HasOne<AlarmEvent>().WithMany().HasForeignKey(x => x.AlarmEventId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<NotificationOutbox>(entity =>
        {
            entity.ToTable("NotificationOutbox");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DedupKey).IsRequired().HasMaxLength(120);
            entity.HasIndex(x => x.DedupKey).IsUnique();
            entity.Property(x => x.Action).IsRequired().HasMaxLength(40);
            entity.Property(x => x.Status).IsRequired().HasMaxLength(20);
            entity.Property(x => x.LastError).HasMaxLength(500);
            entity.HasIndex(x => new { x.Status, x.NextAttemptTs });
            entity.HasOne<AlarmEvent>().WithMany().HasForeignKey(x => x.AlarmEventId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<GatewayInboxBatch>(entity =>
        {
            entity.ToTable("GatewayInbox");
            entity.HasKey(x => new { x.GatewayId, x.StreamId, x.Sequence });
            entity.Property(x => x.Revision).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.ReceivedTs);
            entity.HasIndex(x => new { x.GatewayId, x.DeadLetteredTs });
            entity.Property(x => x.ProcessingError).HasMaxLength(500);
        });
        modelBuilder.Entity<LocalControlCommand>(entity =>
        {
            entity.ToTable("LocalControlCommand");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UserName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.State).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Attempt).IsConcurrencyToken();
            entity.HasIndex(x => x.CreatedTs);
        });
        modelBuilder.Entity<GatewayControlCommand>(entity =>
        {
            entity.ToTable("GatewayCommand");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Revision).HasMaxLength(64).IsRequired();
            entity.Property(x => x.State).HasMaxLength(16).IsRequired();
            entity.Property(x => x.UserName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Text).HasMaxLength(4000);
            entity.Property(x => x.ExecutionTokenHash).HasMaxLength(64);
            entity.Property(x => x.ReceiptHash).HasMaxLength(64);
            entity.HasIndex(x => new { x.GatewayId, x.SessionId, x.State, x.ExpiresTs });
        });
        modelBuilder.Entity<McpIdempotencyRecord>(entity =>
        {
            entity.ToTable("McpIdempotencyRecord");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Tool).IsRequired().HasMaxLength(64);
            entity.HasIndex(x => new { x.Tool, x.RequestId }).IsUnique();
            entity.HasIndex(x => x.CreatedTs);
        });
        modelBuilder.Entity<OeeLineSample>(entity =>
        {
            entity.ToTable("OeeLineSample");
            entity.HasKey(x => x.LineId);
        });
        modelBuilder.Entity<OeeRollup>(entity =>
        {
            entity.ToTable("OeeRollup");
            entity.HasIndex(x => new { x.Granularity, x.BucketTs, x.LineId, x.ProductId }).IsUnique();
            entity.HasIndex(x => new { x.LineId, x.Granularity, x.BucketTs });
        });
        modelBuilder.Entity<OeeRollupState>(entity =>
        {
            entity.ToTable("OeeRollupState");
            entity.Property(x => x.LastError).HasMaxLength(500);
        });
        modelBuilder.Entity<SpcSample>(entity =>
        {
            entity.ToTable("SpcSample");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ValuesJson).IsRequired().HasMaxLength(4000).HasDefaultValue("[]");
            entity.Property(x => x.RuleCodes).IsRequired().HasMaxLength(200).HasDefaultValue("");
            entity.HasIndex(x => new { x.ChartId, x.Ts });
            entity.HasIndex(x => new { x.ChartId, x.Revision, x.Ts });
        });
        modelBuilder.Entity<SpcViolation>(entity =>
        {
            entity.ToTable("SpcViolation");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Message).IsRequired().HasMaxLength(300).HasDefaultValue("");
            entity.HasIndex(x => new { x.ChartId, x.RecoveredTs });
            entity.HasIndex(x => new { x.ChartId, x.Ts });
            entity.HasIndex(x => x.SampleId);
            entity.HasIndex(x => x.AlarmEventId);
        });
        // 下列表从配置库迁入。跨库外键无法在 SQLite 上保留，只保留同行键与索引；
        // 配置侧删除模板/产线/任务时由对应服务清理运行行。
        modelBuilder.Entity<TraceabilityRecord>(entity =>
        {
            entity.ToTable("TraceabilityRecord"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Sn).IsRequired().HasMaxLength(200);
            entity.Property(x => x.JobName).IsRequired().HasMaxLength(100);
            entity.HasIndex(x => x.CycleId).IsUnique();
            entity.HasIndex(x => new { x.Sn, x.Ts });
            entity.HasIndex(x => new { x.AreaId, x.Ts });
            entity.HasIndex(x => new { x.AreaId, x.Outcome, x.Ts });
            entity.HasIndex(x => x.JobId);
        });
        modelBuilder.Entity<TraceabilityAttempt>(entity =>
        {
            entity.ToTable("TraceabilityAttempt"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.JobId).IsUnique().HasFilter("\"ClosedTs\" IS NULL");
            entity.HasIndex(x => x.JobId);
        });
        modelBuilder.Entity<RecipeDownload>(entity =>
        {
            entity.ToTable("RecipeDownloads");
            entity.HasIndex(x => new { x.RecipeId, x.CreatedTs }); entity.HasIndex(x => x.Status);
            entity.Property(x => x.RecipeName).HasMaxLength(100); entity.Property(x => x.RequestedBy).HasMaxLength(100);
            entity.Property(x => x.ApprovedBy).HasMaxLength(100); entity.Property(x => x.Message).HasMaxLength(1000);
        });
        modelBuilder.Entity<ReportRun>(entity =>
        {
            entity.ToTable("ReportRuns");
            entity.HasIndex(x => new { x.TemplateId, x.PeriodKey, x.Version }).IsUnique();
            entity.HasIndex(x => x.GeneratedTs);
            entity.Property(x => x.SignerThumbprint).HasMaxLength(64); entity.Property(x => x.SignerSubject).HasMaxLength(300); entity.Property(x => x.SignatureError).HasMaxLength(500);
        });
        modelBuilder.Entity<ReportApproval>(entity =>
        {
            entity.ToTable("ReportApprovals");
            entity.HasIndex(x => new { x.RunId, x.Ts });
            entity.Property(x => x.Step).HasMaxLength(16); entity.Property(x => x.Decision).HasMaxLength(16);
            entity.Property(x => x.UserName).HasMaxLength(100); entity.Property(x => x.DisplayName).HasMaxLength(100); entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.SignerThumbprint).HasMaxLength(64);
            entity.HasOne<ReportRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CalcJobState>(entity =>
        {
            entity.ToTable("CalcJobStates");
            entity.HasKey(x => x.JobId);
            entity.HasIndex(x => x.JobId);
        });
        modelBuilder.Entity<CalcJobSnapshot>(entity =>
        {
            entity.ToTable("CalcJobSnapshots");
            entity.HasIndex(x => new { x.JobId, x.Ts });
        });
        modelBuilder.Entity<OperationLog>(entity =>
        {
            entity.ToTable("OperationLog");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UserName).IsRequired().HasMaxLength(100);
            entity.Property(x => x.Action).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Result).HasMaxLength(500);
            entity.Property(x => x.TargetType).HasMaxLength(50);
            entity.Property(x => x.TargetId).HasMaxLength(100);
            entity.HasIndex(x => x.Ts);
        });
        modelBuilder.Entity<QualityInspection>(entity =>
        {
            entity.ToTable("QualityInspection");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Sn).HasMaxLength(200).HasDefaultValue("");
            entity.Property(x => x.BatchNo).HasMaxLength(100).HasDefaultValue("");
            entity.Property(x => x.Product).HasMaxLength(100).HasDefaultValue("");
            entity.Property(x => x.Inspector).HasMaxLength(100).HasDefaultValue("");
            entity.Property(x => x.Note).HasMaxLength(500).HasDefaultValue("");
            entity.Property(x => x.CreatedBy).HasMaxLength(100).HasDefaultValue("");
            entity.HasIndex(x => new { x.AreaId, x.Ts });
            entity.HasIndex(x => new { x.Sn, x.Ts });
            entity.HasIndex(x => new { x.BatchNo, x.Ts });
            entity.HasIndex(x => new { x.Result, x.Ts });
            entity.HasIndex(x => x.DefectCodeId);
            entity.HasIndex(x => x.DeviceId);
            entity.HasIndex(x => x.TagId);
        });
        modelBuilder.Entity<DowntimeEvent>(entity =>
        {
            entity.ToTable("DowntimeEvent");
            entity.Property(x => x.Source).IsRequired().HasMaxLength(20);
            entity.Property(x => x.Note).HasMaxLength(500);
            entity.Property(x => x.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(x => x.UpdatedBy).HasMaxLength(100);
            entity.HasIndex(x => new { x.LineId, x.StartTs });
            entity.HasIndex(x => new { x.LineId, x.EndTs });
            entity.HasIndex(x => x.AlarmEventId);
        });
        modelBuilder.Entity<ProductionCountCorrection>(entity =>
        {
            entity.ToTable("ProductionCountCorrection");
            entity.Property(x => x.Reason).IsRequired().HasMaxLength(500);
            entity.Property(x => x.CreatedBy).IsRequired().HasMaxLength(100);
            entity.HasIndex(x => new { x.LineId, x.PeriodFrom });
        });
        modelBuilder.Entity<ControlSequenceRun>(entity =>
        {
            entity.ToTable("ControlSequenceRuns");
            entity.HasIndex(x => new { x.SequenceId, x.StartedTs });
            entity.Property(x => x.SequenceName).HasMaxLength(100); entity.Property(x => x.StartedBy).HasMaxLength(100);
            entity.Property(x => x.ApprovedBy).HasMaxLength(100); entity.Property(x => x.Message).HasMaxLength(500);
        });
        modelBuilder.Entity<ControlRequest>(entity =>
        {
            entity.ToTable("ControlRequests");
            entity.HasIndex(x => new { x.Status, x.ExpiresTs });
            entity.Property(x => x.Kind).HasMaxLength(16); entity.Property(x => x.Source).HasMaxLength(200);
            entity.Property(x => x.RequestedBy).HasMaxLength(100); entity.Property(x => x.DecidedBy).HasMaxLength(100);
            entity.Property(x => x.Comment).HasMaxLength(200); entity.Property(x => x.ResultMessage).HasMaxLength(500);
        });
        modelBuilder.Entity<ControlLockout>(entity =>
        {
            entity.ToTable("ControlLockouts");
            entity.HasIndex(x => x.RemovedTs);
            entity.Property(x => x.Reason).HasMaxLength(200); entity.Property(x => x.RemoveReason).HasMaxLength(200);
            entity.Property(x => x.PlacedBy).HasMaxLength(100); entity.Property(x => x.RemovedBy).HasMaxLength(100);
        });
        modelBuilder.Entity<DeviceControlState>(entity =>
        {
            entity.ToTable("DeviceControlStates");
            entity.HasKey(x => x.DeviceId);
            entity.Property(x => x.Reason).HasMaxLength(200); entity.Property(x => x.ChangedBy).HasMaxLength(100);
        });
        modelBuilder.Entity<AnnunciatorSilence>(entity =>
        {
            entity.ToTable("AnnunciatorSilence");
            entity.HasKey(x => x.PanelId);
            entity.Property(x => x.ByUser).IsRequired().HasMaxLength(100);
            entity.Property(x => x.Reason).HasMaxLength(500);
        });
        modelBuilder.Entity<GatewayDataStream>(entity =>
        {
            entity.ToTable("GatewayStream"); entity.HasKey(x => new { x.GatewayId, x.StreamId });
            entity.Property(x => x.LastPayloadHash).HasMaxLength(64);
            entity.HasIndex(x => x.GatewayId);
        });
        modelBuilder.Entity<GatewayArchiveCursor>(entity =>
        {
            entity.ToTable("GatewayArchiveCursor"); entity.HasKey(x => new { x.GatewayId, x.StreamId, x.TagId });
            entity.HasOne<GatewayDataStream>().WithMany().HasForeignKey(x => new { x.GatewayId, x.StreamId }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<GatewayDeviceHealth>(entity =>
        {
            entity.ToTable("GatewayDeviceHealth"); entity.HasKey(x => x.DeviceId);
            entity.Property(x => x.ExpectedRevision).HasMaxLength(64).IsRequired(); entity.Property(x => x.ObservedRevision).HasMaxLength(64);
            entity.Property(x => x.Error).HasMaxLength(1000); entity.HasIndex(x => x.GatewayId);
        });
    }
}
