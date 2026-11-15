using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class OeeRollupConfiguration : IEntityTypeConfiguration<OeeRollup>
{
    public void Configure(EntityTypeBuilder<OeeRollup> entity)
    {
        entity.ToTable("OeeRollup");
        entity.HasIndex(x => new { x.Granularity, x.BucketTs, x.LineId, x.ProductId }).IsUnique();
        entity.HasIndex(x => new { x.LineId, x.Granularity, x.BucketTs });
    }
}
