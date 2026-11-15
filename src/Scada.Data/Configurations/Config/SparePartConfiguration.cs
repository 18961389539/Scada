using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class SparePartConfiguration : IEntityTypeConfiguration<SparePart>
{
    public void Configure(EntityTypeBuilder<SparePart> entity)
    {
        entity.ToTable("SparePart");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Code).IsRequired().HasMaxLength(64);
        entity.HasIndex(x => x.Code).IsUnique();
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Specification).HasMaxLength(128).HasDefaultValue("");
        entity.Property(x => x.Unit).HasMaxLength(32).HasDefaultValue("");
        entity.Property(x => x.Location).HasMaxLength(128).HasDefaultValue("");
        entity.Property(x => x.Supplier).HasMaxLength(128).HasDefaultValue("");
        entity.Property(x => x.Description).HasMaxLength(500).HasDefaultValue("");
        entity.HasIndex(x => x.Enabled);
    }
}
