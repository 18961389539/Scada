using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class DeviceObjectInstanceConfiguration : IEntityTypeConfiguration<DeviceObjectInstance>
{
    public void Configure(EntityTypeBuilder<DeviceObjectInstance> entity)
    {
        entity.ToTable("ObjectInstance"); entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).HasMaxLength(40).IsRequired();
        entity.Property(x => x.StartAddress).HasMaxLength(80).IsRequired().HasDefaultValue("");
        entity.Property(x => x.UpdatedBy).HasMaxLength(100).IsRequired();
        entity.HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
        entity.Property(x => x.Revision).IsConcurrencyToken();
        entity.HasOne<DeviceObjectTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ProcessScreen>().WithMany().HasForeignKey(x => x.ScreenId).OnDelete(DeleteBehavior.Restrict);
    }
}
