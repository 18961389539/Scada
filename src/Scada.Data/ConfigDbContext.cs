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
    public DbSet<AlarmRule> AlarmRules => Set<AlarmRule>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ProcessScreen> ProcessScreens => Set<ProcessScreen>();
    public DbSet<DeviceObjectTemplate> ObjectTemplates => Set<DeviceObjectTemplate>();
    public DbSet<DeviceObjectTemplateVersion> ObjectTemplateVersions => Set<DeviceObjectTemplateVersion>();
    public DbSet<DeviceObjectInstance> ObjectInstances => Set<DeviceObjectInstance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            entity.Property(x => x.BaudRate).HasDefaultValue(9600);
            entity.Property(x => x.DataBits).HasDefaultValue(8);
            entity.Property(x => x.StopBits).HasDefaultValue(Scada.Core.Enums.SerialStopBits.One);
            entity.Property(x => x.Cpu).HasDefaultValue(Scada.Core.Enums.S7Cpu.S71200).HasSentinel((Scada.Core.Enums.S7Cpu)(-1));
            entity.Property(x => x.Slot).HasDefaultValue((short)1).HasSentinel((short)-1);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.ToTable("Tag");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
            entity.Property(x => x.Description).HasMaxLength(500);
            entity.Property(x => x.Unit).HasMaxLength(32);
            entity.Property(x => x.DbNumber).HasDefaultValue(1);
            entity.Property(x => x.Scale).HasDefaultValue(1.0).HasSentinel(double.NaN);
            entity.Property(x => x.ArchivePeriodSec).HasDefaultValue(600).HasSentinel(-1);
            entity.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
            entity.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlarmRule>(entity =>
        {
            entity.ToTable("AlarmRule");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Message).HasMaxLength(500);
            entity.HasIndex(x => new { x.TagId, x.Type });
            entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlarmEvent>(entity =>
        {
            entity.ToTable("AlarmEvent");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Message).IsRequired().HasMaxLength(500);
            entity.Property(x => x.AckUser).HasMaxLength(100);
            entity.HasIndex(x => x.StartTs);
            entity.HasIndex(x => new { x.TagId, x.StartTs });
            entity.HasIndex(x => x.EndTs);
        });

        modelBuilder.Entity<OperationLog>(entity =>
        {
            entity.ToTable("OperationLog");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UserName).IsRequired().HasMaxLength(100);
            entity.Property(x => x.Action).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Result).HasMaxLength(500);
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
