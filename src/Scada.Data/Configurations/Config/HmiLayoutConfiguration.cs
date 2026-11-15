using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class HmiLayoutConfiguration : IEntityTypeConfiguration<HmiLayout>
{
    public void Configure(EntityTypeBuilder<HmiLayout> entity)
    {
        entity.ToTable("HmiLayout"); entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100); entity.HasIndex(x => x.Name).IsUnique();
        entity.Property(x => x.UpdatedBy).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Revision).IsConcurrencyToken();
        entity.Property(x => x.PublishedName).HasMaxLength(100);
        entity.Property(x => x.PublishedBy).HasMaxLength(100);
    }
}
