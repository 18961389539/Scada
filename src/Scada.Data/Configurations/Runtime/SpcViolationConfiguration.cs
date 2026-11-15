using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class SpcViolationConfiguration : IEntityTypeConfiguration<SpcViolation>
{
    public void Configure(EntityTypeBuilder<SpcViolation> entity)
    {
        entity.ToTable("SpcViolation");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Message).IsRequired().HasMaxLength(300).HasDefaultValue("");
        entity.HasIndex(x => new { x.ChartId, x.RecoveredTs });
        entity.HasIndex(x => new { x.ChartId, x.Ts });
        entity.HasIndex(x => x.SampleId);
        entity.HasIndex(x => x.AlarmEventId);
    }
}
