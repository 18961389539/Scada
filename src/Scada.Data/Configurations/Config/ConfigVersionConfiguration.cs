using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ConfigVersionConfiguration : IEntityTypeConfiguration<ConfigVersion>
{
    public void Configure(EntityTypeBuilder<ConfigVersion> entity)
    {
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Kind).HasMaxLength(32).IsRequired(); entity.Property(x => x.Note).HasMaxLength(500);
        entity.Property(x => x.CreatedBy).HasMaxLength(100); entity.Property(x => x.ContentSha256).HasMaxLength(64);
        entity.HasIndex(x => x.CreatedTs);
    }
}
