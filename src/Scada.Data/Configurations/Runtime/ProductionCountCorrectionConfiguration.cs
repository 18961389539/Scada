using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ProductionCountCorrectionConfiguration : IEntityTypeConfiguration<ProductionCountCorrection>
{
    public void Configure(EntityTypeBuilder<ProductionCountCorrection> entity)
    {
        entity.ToTable("ProductionCountCorrection");
        entity.Property(x => x.Reason).IsRequired().HasMaxLength(500);
        entity.Property(x => x.CreatedBy).IsRequired().HasMaxLength(100);
        entity.HasIndex(x => new { x.LineId, x.PeriodFrom });
    }
}
