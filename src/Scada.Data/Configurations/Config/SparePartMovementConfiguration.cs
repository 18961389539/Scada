using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class SparePartMovementConfiguration : IEntityTypeConfiguration<SparePartMovement>
{
    public void Configure(EntityTypeBuilder<SparePartMovement> entity)
    {
        entity.ToTable("SparePartMovement");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Operator).HasMaxLength(100).HasDefaultValue("");
        entity.Property(x => x.Note).HasMaxLength(500).HasDefaultValue("");
        entity.HasIndex(x => new { x.PartId, x.Ts });
        entity.HasIndex(x => x.WorkOrderId);
        entity.HasOne<SparePart>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
    }
}
