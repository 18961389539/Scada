using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class DeviceObjectTemplateVersionConfiguration : IEntityTypeConfiguration<DeviceObjectTemplateVersion>
{
    public void Configure(EntityTypeBuilder<DeviceObjectTemplateVersion> entity)
    {
        entity.ToTable("ObjectTemplateVersion"); entity.HasKey(x => x.Id);
        entity.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        entity.HasIndex(x => new { x.TemplateId, x.Version }).IsUnique();
        entity.HasOne<DeviceObjectTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
    }
}
