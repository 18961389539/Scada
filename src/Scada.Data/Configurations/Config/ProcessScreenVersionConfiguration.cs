using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ProcessScreenVersionConfiguration : IEntityTypeConfiguration<ProcessScreenVersion>
{
    public void Configure(EntityTypeBuilder<ProcessScreenVersion> entity)
    {
        entity.ToTable("ProcessScreenVersion"); entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.Property(x => x.PublishedBy).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Note).IsRequired().HasMaxLength(500);
        entity.Property(x => x.Kind).IsRequired().HasMaxLength(20);
        entity.HasIndex(x => new { x.ScreenId, x.Revision }).IsUnique();
        entity.HasOne<ProcessScreen>().WithMany().HasForeignKey(x => x.ScreenId).OnDelete(DeleteBehavior.Cascade);
    }
}
