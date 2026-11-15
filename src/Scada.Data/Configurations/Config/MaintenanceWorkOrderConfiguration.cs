using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class MaintenanceWorkOrderConfiguration : IEntityTypeConfiguration<MaintenanceWorkOrder>
{
    public void Configure(EntityTypeBuilder<MaintenanceWorkOrder> entity)
    {
        entity.ToTable("MaintenanceWorkOrder");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Number).IsRequired().HasMaxLength(40);
        entity.HasIndex(x => x.Number).IsUnique();
        entity.Property(x => x.Title).IsRequired().HasMaxLength(200);
        entity.Property(x => x.Description).HasMaxLength(2000).HasDefaultValue("");
        entity.Property(x => x.Resolution).HasMaxLength(2000).HasDefaultValue("");
        entity.Property(x => x.Assignee).HasMaxLength(100).HasDefaultValue("");
        entity.Property(x => x.Reporter).HasMaxLength(100).HasDefaultValue("");
        entity.Property(x => x.UpdatedBy).HasMaxLength(100).HasDefaultValue("");
        entity.HasIndex(x => x.Status);
        entity.HasIndex(x => x.AssetId);
        entity.HasIndex(x => x.DeviceId);
        entity.HasIndex(x => new { x.AreaId, x.Status });
        entity.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.SetNull);
    }
}
