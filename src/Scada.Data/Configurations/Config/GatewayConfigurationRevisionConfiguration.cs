using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class GatewayConfigurationRevisionConfiguration : IEntityTypeConfiguration<GatewayConfigurationRevision>
{
    public void Configure(EntityTypeBuilder<GatewayConfigurationRevision> entity)
    {
        entity.ToTable("GatewayConfiguration"); entity.HasKey(x => x.Id);
        entity.Property(x => x.Revision).HasMaxLength(64).IsRequired(); entity.HasIndex(x => new { x.GatewayId, x.Revision }).IsUnique();
        entity.HasOne<AcquisitionGateway>().WithMany().HasForeignKey(x => x.GatewayId).OnDelete(DeleteBehavior.Cascade);
    }
}
