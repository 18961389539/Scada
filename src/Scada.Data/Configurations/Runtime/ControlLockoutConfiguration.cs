using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ControlLockoutConfiguration : IEntityTypeConfiguration<ControlLockout>
{
    public void Configure(EntityTypeBuilder<ControlLockout> entity)
    {
        entity.ToTable("ControlLockouts");
        entity.HasIndex(x => x.RemovedTs);
        entity.Property(x => x.Reason).HasMaxLength(200); entity.Property(x => x.RemoveReason).HasMaxLength(200);
        entity.Property(x => x.PlacedBy).HasMaxLength(100); entity.Property(x => x.RemovedBy).HasMaxLength(100);
    }
}
