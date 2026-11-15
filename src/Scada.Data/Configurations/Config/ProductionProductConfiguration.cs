using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ProductionProductConfiguration : IEntityTypeConfiguration<ProductionProduct>
{
    public void Configure(EntityTypeBuilder<ProductionProduct> entity)
    {
        entity.ToTable("ProductionProduct");
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.HasIndex(x => x.Name).IsUnique();
        entity.Property(x => x.Code).HasMaxLength(60);
        entity.Property(x => x.Description).HasMaxLength(500);
        entity.Property(x => x.UpdatedBy).HasMaxLength(100);
        entity.HasIndex(x => x.AreaId);
        entity.HasOne<Area>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(x => x.Revision).IsConcurrencyToken();
    }
}
