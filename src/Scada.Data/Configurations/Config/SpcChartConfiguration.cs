using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class SpcChartConfiguration : IEntityTypeConfiguration<SpcChart>
{
    public void Configure(EntityTypeBuilder<SpcChart> entity)
    {
        entity.ToTable("SpcChart");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Description).HasMaxLength(500).HasDefaultValue("");
        entity.Property(x => x.UpdatedBy).HasMaxLength(100).HasDefaultValue("");
        entity.Property(x => x.ConfigVersion).IsConcurrencyToken();
        entity.HasIndex(x => x.AreaId);
        entity.HasIndex(x => x.TagId);
        entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
    }
}
