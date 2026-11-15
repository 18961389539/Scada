using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ControlPointConfiguration : IEntityTypeConfiguration<ControlPoint>
{
    public void Configure(EntityTypeBuilder<ControlPoint> entity)
    {
        entity.HasIndex(x => x.TagId).IsUnique();
        entity.Property(x => x.PermitExpression).HasMaxLength(4096); entity.Property(x => x.FeedbackExpression).HasMaxLength(4096);
        entity.Property(x => x.PermitMessage).HasMaxLength(200); entity.Property(x => x.UpdatedBy).HasMaxLength(100);
        entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
    }
}
