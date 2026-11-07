using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Scada.Core.Entities;
using Scada.Data.Infrastructure;

namespace Scada.Data;

/// <summary>
/// 运行数据：报警流水、通知发件箱、网关收件箱、控制命令账本、MCP 幂等记录和 OEE 采样。
/// 与配置库分开，避免配置保存和运行写入抢同一把 SQLite 写锁。
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
    }
}
