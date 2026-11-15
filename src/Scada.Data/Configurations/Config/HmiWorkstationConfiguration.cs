using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class HmiWorkstationConfiguration : IEntityTypeConfiguration<HmiWorkstation>
{
    public void Configure(EntityTypeBuilder<HmiWorkstation> entity)
    {
        entity.ToTable("HmiWorkstation"); entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(60); entity.HasIndex(x => x.Name).IsUnique();
        entity.Property(x => x.Description).HasMaxLength(200);
        entity.Property(x => x.UpdatedBy).IsRequired().HasMaxLength(100);
        entity.Property(x => x.UpdatedTs).IsConcurrencyToken();
        entity.HasOne<HmiLayout>().WithMany().HasForeignKey(x => x.LayoutId).OnDelete(DeleteBehavior.SetNull);
    }
}
