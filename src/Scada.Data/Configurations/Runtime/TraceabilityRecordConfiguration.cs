using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class TraceabilityRecordConfiguration : IEntityTypeConfiguration<TraceabilityRecord>
{
    public void Configure(EntityTypeBuilder<TraceabilityRecord> entity)
    {
        entity.ToTable("TraceabilityRecord"); entity.HasKey(x => x.Id);
        entity.Property(x => x.Sn).IsRequired().HasMaxLength(200);
        entity.Property(x => x.JobName).IsRequired().HasMaxLength(100);
        entity.HasIndex(x => x.CycleId).IsUnique();
        entity.HasIndex(x => new { x.Sn, x.Ts });
        entity.HasIndex(x => new { x.AreaId, x.Ts });
        entity.HasIndex(x => new { x.AreaId, x.Outcome, x.Ts });
        entity.HasIndex(x => x.JobId);
    }
}
