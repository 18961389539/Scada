using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class GatewayDataStreamConfiguration : IEntityTypeConfiguration<GatewayDataStream>
{
    public void Configure(EntityTypeBuilder<GatewayDataStream> entity)
    {
        entity.ToTable("GatewayStream"); entity.HasKey(x => new { x.GatewayId, x.StreamId });
        entity.Property(x => x.LastPayloadHash).HasMaxLength(64);
        entity.HasIndex(x => x.GatewayId);
    }
}
