using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> entity)
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
        entity.HasIndex(x => new { x.JobId, x.JobOutput });
    }
}
