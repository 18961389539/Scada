using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class GatewayInboxBatchConfiguration : IEntityTypeConfiguration<GatewayInboxBatch>
{
    public void Configure(EntityTypeBuilder<GatewayInboxBatch> entity)
    {
        entity.ToTable("GatewayInbox");
        entity.HasKey(x => new { x.GatewayId, x.StreamId, x.Sequence });
        entity.Property(x => x.Revision).HasMaxLength(64).IsRequired();
        entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        entity.HasIndex(x => x.ReceivedTs);
        entity.HasIndex(x => new { x.GatewayId, x.DeadLetteredTs });
        entity.Property(x => x.ProcessingError).HasMaxLength(500);
    }
}
