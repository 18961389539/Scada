using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class QualityInspectionConfiguration : IEntityTypeConfiguration<QualityInspection>
{
    public void Configure(EntityTypeBuilder<QualityInspection> entity)
    {
        entity.ToTable("QualityInspection");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Sn).HasMaxLength(200).HasDefaultValue("");
        entity.Property(x => x.BatchNo).HasMaxLength(100).HasDefaultValue("");
        entity.Property(x => x.Product).HasMaxLength(100).HasDefaultValue("");
        entity.Property(x => x.Inspector).HasMaxLength(100).HasDefaultValue("");
        entity.Property(x => x.Note).HasMaxLength(500).HasDefaultValue("");
        entity.Property(x => x.CreatedBy).HasMaxLength(100).HasDefaultValue("");
        entity.Property(x => x.UpdatedBy).HasMaxLength(100).HasDefaultValue("");
        entity.HasIndex(x => new { x.AreaId, x.Ts });
        entity.HasIndex(x => new { x.Sn, x.Ts });
        entity.HasIndex(x => new { x.BatchNo, x.Ts });
        entity.HasIndex(x => new { x.Result, x.Ts });
        entity.HasIndex(x => x.DefectCodeId);
        entity.HasIndex(x => x.DeviceId);
        entity.HasIndex(x => x.TagId);
    }
}
