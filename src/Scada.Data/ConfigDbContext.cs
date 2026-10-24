using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;

namespace Scada.Data;

public class ConfigDbContext : DbContext
{
    public ConfigDbContext(DbContextOptions<ConfigDbContext> options) : base(options)
    {
    }

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagDependency> TagDependencies => Set<TagDependency>();
    public DbSet<AlarmRule> AlarmRules => Set<AlarmRule>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();
    public DbSet<AlarmShelve> AlarmShelves => Set<AlarmShelve>();
    public DbSet<AlarmActivity> AlarmActivities => Set<AlarmActivity>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationRoute> NotificationRoutes => Set<NotificationRoute>();
    public DbSet<DutyShift> DutyShifts => Set<DutyShift>();
    public DbSet<AlarmNotificationLog> AlarmNotificationLogs => Set<AlarmNotificationLog>();
    public DbSet<NotificationOutbox> NotificationOutbox => Set<NotificationOutbox>();
    public DbSet<CalcJob> CalcJobs => Set<CalcJob>();
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();
    public DbSet<ReportRun> ReportRuns => Set<ReportRun>();
    public DbSet<ConfigVersion> ConfigVersions => Set<ConfigVersion>();
    public DbSet<CalcJobState> CalcJobStates => Set<CalcJobState>();
    public DbSet<CalcJobSnapshot> CalcJobSnapshots => Set<CalcJobSnapshot>();
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<AccessRole> AccessRoles => Set<AccessRole>();
    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();
    public DbSet<ProcessScreen> ProcessScreens => Set<ProcessScreen>();
    public DbSet<DeviceObjectTemplate> ObjectTemplates => Set<DeviceObjectTemplate>();
    public DbSet<DeviceObjectTemplateVersion> ObjectTemplateVersions => Set<DeviceObjectTemplateVersion>();
    public DbSet<DeviceObjectInstance> ObjectInstances => Set<DeviceObjectInstance>();
    public DbSet<AcquisitionGateway> Gateways => Set<AcquisitionGateway>();
    public DbSet<GatewayConfigurationRevision> GatewayConfigurations => Set<GatewayConfigurationRevision>();
    public DbSet<GatewayDataStream> GatewayStreams => Set<GatewayDataStream>();
    public DbSet<GatewayInboxBatch> GatewayInbox => Set<GatewayInboxBatch>();
    public DbSet<GatewayArchiveCursor> GatewayArchiveCursors => Set<GatewayArchiveCursor>();
    public DbSet<GatewayControlCommand> GatewayCommands => Set<GatewayControlCommand>();
    public DbSet<GatewayDeviceHealth> GatewayDeviceHealth => Set<GatewayDeviceHealth>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess) { AssignKeys(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) { AssignKeys(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }

    // 新增报警规则未指定稳定键时生成一个（API、模板实例化、初始化数据都不必关心）。
    private void AssignKeys()
    {
        foreach (var entry in ChangeTracker.Entries<AlarmRule>())
            if (entry.State is EntityState.Added or EntityState.Modified && string.IsNullOrWhiteSpace(entry.Entity.Key)) entry.Entity.Key = Guid.NewGuid().ToString("N");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Area>(entity =>
        {
            entity.ToTable("Area"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100); entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Description).HasMaxLength(500);
            // 旧库升级时已有设备落入默认区域。
            entity.HasData(new Area { Id = Area.DefaultId, Name = "默认区域", Description = "升级前的设备与未指定区域的设备" });
        });
        modelBuilder.Entity<AccessRole>(entity =>
        {
            entity.ToTable("AccessRole"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100); entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.Description).HasMaxLength(500);
        });
        modelBuilder.Entity<UserRoleAssignment>(entity =>
        {
            entity.ToTable("UserRoleAssignment"); entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.RoleId, x.AreaId }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AccessRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Area>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TagDependency>(entity =>
        {
            entity.ToTable("TagDependency"); entity.HasKey(x => new { x.TagId, x.InputTagId });
            entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.InputTagId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<GatewayDeviceHealth>(entity =>
        {
            entity.ToTable("GatewayDeviceHealth"); entity.HasKey(x => x.DeviceId);
            entity.Property(x => x.ExpectedRevision).HasMaxLength(64).IsRequired(); entity.Property(x => x.ObservedRevision).HasMaxLength(64);
            entity.Property(x => x.Error).HasMaxLength(1000); entity.HasIndex(x => x.GatewayId);
        });
        modelBuilder.Entity<GatewayControlCommand>(entity =>
        {
            entity.ToTable("GatewayCommand"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Revision).HasMaxLength(64).IsRequired(); entity.Property(x => x.State).HasMaxLength(16).IsRequired();
            entity.Property(x => x.UserName).HasMaxLength(100).IsRequired(); entity.Property(x => x.ExecutionTokenHash).HasMaxLength(64);
            entity.Property(x => x.ReceiptHash).HasMaxLength(64); entity.HasIndex(x => new { x.GatewayId, x.SessionId, x.State, x.ExpiresTs });
            entity.HasOne<AcquisitionGateway>().WithMany().HasForeignKey(x => x.GatewayId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AcquisitionGateway>(entity =>
        {
            entity.ToTable("AcquisitionGateway"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired(); entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.AgentVersion).HasMaxLength(64); entity.Property(x => x.AppliedRevision).HasMaxLength(64);
            entity.Property(x => x.CapabilitiesJson).HasMaxLength(512).HasDefaultValue("[]");
            entity.Property(x => x.LastError).HasMaxLength(1000);
        });
        modelBuilder.Entity<GatewayConfigurationRevision>(entity =>
        {
            entity.ToTable("GatewayConfiguration"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Revision).HasMaxLength(64).IsRequired(); entity.HasIndex(x => new { x.GatewayId, x.Revision }).IsUnique();
            entity.HasOne<AcquisitionGateway>().WithMany().HasForeignKey(x => x.GatewayId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<GatewayDataStream>(entity =>
        {
            entity.ToTable("GatewayStream"); entity.HasKey(x => new { x.GatewayId, x.StreamId });
            entity.Property(x => x.LastPayloadHash).HasMaxLength(64);
            entity.HasOne<AcquisitionGateway>().WithMany().HasForeignKey(x => x.GatewayId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<GatewayInboxBatch>(entity =>
        {
            entity.ToTable("GatewayInbox"); entity.HasKey(x => new { x.GatewayId, x.StreamId, x.Sequence });
            entity.Property(x => x.Revision).HasMaxLength(64).IsRequired(); entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.ReceivedTs);
            entity.Property(x => x.ProcessingError).HasMaxLength(500);
            entity.HasOne<GatewayDataStream>().WithMany().HasForeignKey(x => new { x.GatewayId, x.StreamId }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<GatewayArchiveCursor>(entity =>
        {
            entity.ToTable("GatewayArchiveCursor"); entity.HasKey(x => new { x.GatewayId, x.StreamId, x.TagId });
            entity.HasOne<GatewayDataStream>().WithMany().HasForeignKey(x => new { x.GatewayId, x.StreamId }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<DeviceObjectTemplate>(entity =>
        {
            entity.ToTable("ObjectTemplate"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique(); entity.Property(x => x.Revision).IsConcurrencyToken();
        });
        modelBuilder.Entity<DeviceObjectTemplateVersion>(entity =>
        {
            entity.ToTable("ObjectTemplateVersion"); entity.HasKey(x => x.Id);
            entity.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => new { x.TemplateId, x.Version }).IsUnique();
            entity.HasOne<DeviceObjectTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<DeviceObjectInstance>(entity =>
        {
            entity.ToTable("ObjectInstance"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(40).IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne<DeviceObjectTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProcessScreen>().WithMany().HasForeignKey(x => x.ScreenId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProcessScreen>(entity =>
        {
            entity.ToTable("ProcessScreen");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
            entity.Property(x => x.PublishedName).HasMaxLength(100);
            entity.Property(x => x.UpdatedBy).IsRequired().HasMaxLength(100);
            entity.Property(x => x.Revision).IsConcurrencyToken();
        });
        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("Device");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.SerialPort).HasMaxLength(128).HasDefaultValue("COM1");
            entity.HasOne<AcquisitionGateway>().WithMany().HasForeignKey(x => x.GatewayId).OnDelete(DeleteBehavior.Restrict);
            // 区域引用不建数据库外键：旧库升级只需追加列、不重建设备表；删除区域前由接口检查无设备引用。
            entity.Property(x => x.AreaId).HasDefaultValue(Area.DefaultId).HasSentinel(0);
            entity.HasIndex(x => x.AreaId);
            entity.Property(x => x.BaudRate).HasDefaultValue(9600);
            entity.Property(x => x.DataBits).HasDefaultValue(8);
            entity.Property(x => x.StopBits).HasDefaultValue(Scada.Core.Enums.SerialStopBits.One);
            entity.Property(x => x.Cpu).HasDefaultValue(Scada.Core.Enums.S7Cpu.S71200).HasSentinel((Scada.Core.Enums.S7Cpu)(-1));
            entity.Property(x => x.Slot).HasDefaultValue((short)1).HasSentinel((short)-1);
            entity.Property(x => x.OpcEndpointUrl).HasMaxLength(1024).HasDefaultValue("opc.tcp://localhost:4840");
            entity.Property(x => x.OpcSecurityMode).HasDefaultValue(Scada.Core.Enums.OpcSecurityMode.SignAndEncrypt).HasSentinel((Scada.Core.Enums.OpcSecurityMode)(-1));
            entity.Property(x => x.OpcUserName).HasMaxLength(200).HasDefaultValue("");
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("Tag", table => table.HasCheckConstraint("CK_Tag_Source", "(Kind = 0 AND DeviceId IS NOT NULL) OR (Kind = 1 AND DeviceId IS NULL AND Writable = 0) OR (Kind = 2 AND DeviceId IS NULL AND Writable = 0 AND JobId IS NOT NULL)"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
            entity.Property(x => x.Description).HasMaxLength(500);
            entity.Property(x => x.Unit).HasMaxLength(32);
            entity.Property(x => x.DbNumber).HasDefaultValue(1);
            entity.Property(x => x.NodeId).HasMaxLength(2048).HasDefaultValue("");
            entity.Property(x => x.Expression).HasMaxLength(4096).HasDefaultValue("");
            entity.Property(x => x.CalculationEnabled).HasDefaultValue(true);
            entity.Property(x => x.CalculationIntervalMs).HasDefaultValue(1000);
            entity.Property(x => x.InputMaxAgeMs).HasDefaultValue(15000);
            entity.Property(x => x.CalculationRevision).HasDefaultValue(1L).IsConcurrencyToken();
            entity.Property(x => x.Scale).HasDefaultValue(1.0).HasSentinel(double.NaN);
            entity.Property(x => x.ArchivePeriodSec).HasDefaultValue(600).HasSentinel(-1);
            entity.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
            entity.HasIndex(x => x.Name).IsUnique().HasFilter("DeviceId IS NULL");
            entity.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.AreaId);
        });

        modelBuilder.Entity<AlarmRule>(entity =>
        {
            entity.ToTable("AlarmRule");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Message).HasMaxLength(500);
            entity.Property(x => x.OutOfServiceReason).HasMaxLength(500);
            entity.HasIndex(x => new { x.TagId, x.Type });
            entity.Property(x => x.Key).HasMaxLength(64).HasDefaultValue("");
            entity.HasIndex(x => new { x.TagId, x.Key }).IsUnique();
            entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlarmEvent>(entity =>
        {
            entity.ToTable("AlarmEvent");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Message).IsRequired().HasMaxLength(500);
            entity.Property(x => x.AckUser).HasMaxLength(100);
            entity.Property(x => x.EndReason).HasMaxLength(100);
            entity.Property(x => x.SuppressReason).HasMaxLength(200);
            entity.HasIndex(x => x.RuleId);
            entity.HasIndex(x => new { x.DeviceId, x.GatewayId }).IsUnique()
                .HasFilter("\"GatewayId\" IS NOT NULL AND \"DeviceId\" IS NOT NULL AND \"RuleId\" IS NULL AND \"TagId\" IS NULL AND \"EndTs\" IS NULL");
            entity.HasIndex(x => x.StartTs);
            entity.HasIndex(x => new { x.TagId, x.StartTs });
            entity.HasIndex(x => x.EndTs);
        });

        modelBuilder.Entity<AlarmShelve>(entity =>
        {
            entity.ToTable("AlarmShelve");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Reason).IsRequired().HasMaxLength(500);
            entity.Property(x => x.User).IsRequired().HasMaxLength(100);
            entity.Property(x => x.ClearedBy).HasMaxLength(100);
            entity.HasIndex(x => new { x.RuleId, x.ClearedTs });
            entity.HasOne<AlarmRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<NotificationChannel>(entity =>
        {
            entity.ToTable("NotificationChannel");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.Property(x => x.SettingsJson).IsRequired().HasMaxLength(4000);
        });

        modelBuilder.Entity<DutyShift>(entity =>
        {
            entity.ToTable("DutyShift");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<NotificationRoute>(entity =>
        {
            entity.ToTable("NotificationRoute");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
            entity.Property(x => x.Recipients).HasMaxLength(2000);
            entity.HasOne<NotificationChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DutyShift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.AreaId);
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

        modelBuilder.Entity<ConfigVersion>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(32).IsRequired(); entity.Property(x => x.Note).HasMaxLength(500);
            entity.Property(x => x.CreatedBy).HasMaxLength(100); entity.Property(x => x.ContentSha256).HasMaxLength(64);
            entity.HasIndex(x => x.CreatedTs);
        });
        modelBuilder.Entity<ReportTemplate>(entity =>
        {
            entity.ToTable("ReportTemplates");
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasOne<Area>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReportRun>(entity =>
        {
            entity.ToTable("ReportRuns");
            entity.HasIndex(x => new { x.TemplateId, x.PeriodKey, x.Version }).IsUnique();
            entity.HasIndex(x => x.GeneratedTs);
            // 模板删除不级联：已生成报表须保留。
        });
        modelBuilder.Entity<CalcJob>(entity =>
        {
            entity.ToTable("CalcJobs");
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasOne<Area>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CalcJobState>(entity =>
        {
            entity.ToTable("CalcJobStates");
            entity.HasKey(x => x.JobId);
            entity.HasOne<CalcJob>().WithOne().HasForeignKey<CalcJobState>(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<CalcJobSnapshot>(entity =>
        {
            entity.ToTable("CalcJobSnapshots");
            entity.HasIndex(x => new { x.JobId, x.Ts });
            entity.HasOne<CalcJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Tag>().HasIndex(x => new { x.JobId, x.JobOutput });
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

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("User");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UserName).IsRequired().HasMaxLength(100);
            entity.Property(x => x.PasswordHash).IsRequired().HasMaxLength(500);
            entity.Property(x => x.DisplayName).HasMaxLength(100);
            entity.HasIndex(x => x.UserName).IsUnique();
        });
    }
}
