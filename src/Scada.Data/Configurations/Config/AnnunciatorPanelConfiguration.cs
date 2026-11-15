using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class AnnunciatorPanelConfiguration : IEntityTypeConfiguration<AnnunciatorPanel>
{
    public void Configure(EntityTypeBuilder<AnnunciatorPanel> entity)
    {
        entity.ToTable("AnnunciatorPanel");
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.HasIndex(x => x.Name).IsUnique();
        entity.Property(x => x.UpdatedBy).HasMaxLength(100);
    }
}
