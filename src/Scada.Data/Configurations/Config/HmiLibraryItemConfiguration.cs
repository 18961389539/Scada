using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class HmiLibraryItemConfiguration : IEntityTypeConfiguration<HmiLibraryItem>
{
    public void Configure(EntityTypeBuilder<HmiLibraryItem> entity)
    {
        entity.ToTable("HmiLibraryItem"); entity.HasKey(x => x.Id);
        entity.Property(x => x.Key).IsRequired().HasMaxLength(64); entity.HasIndex(x => x.Key).IsUnique();
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Category).IsRequired().HasMaxLength(40);
        entity.Property(x => x.Kind).IsRequired().HasMaxLength(16);
        entity.Property(x => x.UpdatedBy).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Revision).IsConcurrencyToken();
    }
}
