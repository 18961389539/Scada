using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class AlarmRollupConfiguration : IEntityTypeConfiguration<AlarmRollup>
{
    public void Configure(EntityTypeBuilder<AlarmRollup> entity)
    {
        entity.ToTable("AlarmRollup");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Source).IsRequired().HasMaxLength(120);
        entity.Property(x => x.Message).IsRequired().HasMaxLength(200);
        entity.Property(x => x.Category).HasDefaultValue(Scada.Core.Enums.AlarmCategory.Process);
        entity.HasIndex(x => new { x.Granularity, x.BucketTs, x.Source, x.Level }).IsUnique();
    }
}
