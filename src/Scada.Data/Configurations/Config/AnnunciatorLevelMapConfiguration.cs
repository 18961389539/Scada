using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class AnnunciatorLevelMapConfiguration : IEntityTypeConfiguration<AnnunciatorLevelMap>
{
    public void Configure(EntityTypeBuilder<AnnunciatorLevelMap> entity)
    {
        entity.ToTable("AnnunciatorLevelMap");
        entity.HasIndex(x => new { x.PanelId, x.Level }).IsUnique();
        entity.Property(x => x.SoundKey).HasMaxLength(40);
        entity.HasOne<AnnunciatorPanel>().WithMany().HasForeignKey(x => x.PanelId).OnDelete(DeleteBehavior.Cascade);
    }
}
