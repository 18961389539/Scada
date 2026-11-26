using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class TraceabilityJobConfiguration : IEntityTypeConfiguration<TraceabilityJob>
{
    public void Configure(EntityTypeBuilder<TraceabilityJob> entity)
    {
        entity.ToTable("TraceabilityJob"); entity.HasKey(x => x.Id);
        entity.Property(x => x.DuplicateCheckWindowDays).HasDefaultValue(10);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.Property(x => x.ScannerOptionsJson).IsRequired().HasDefaultValue("{}");
        entity.Property(x => x.DuplicateCheckEnabled).HasDefaultValue(false);
        entity.HasIndex(x => x.TriggerTagId).IsUnique();
        entity.Property(x => x.Revision).IsConcurrencyToken();
        entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.TriggerTagId).OnDelete(DeleteBehavior.Restrict);
    }
}
