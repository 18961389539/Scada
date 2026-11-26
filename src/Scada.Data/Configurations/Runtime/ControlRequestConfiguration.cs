using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ControlRequestConfiguration : IEntityTypeConfiguration<ControlRequest>
{
    public void Configure(EntityTypeBuilder<ControlRequest> entity)
    {
        entity.ToTable("ControlRequests");
        entity.HasIndex(x => new { x.Status, x.ExpiresTs });
        entity.Property(x => x.Kind).HasMaxLength(16); entity.Property(x => x.Source).HasMaxLength(200);
        entity.Property(x => x.RequestedBy).HasMaxLength(100); entity.Property(x => x.DecidedBy).HasMaxLength(100);
        entity.Property(x => x.Comment).HasMaxLength(200); entity.Property(x => x.ResultMessage).HasMaxLength(500);
        entity.Property(x => x.Text).HasMaxLength(2000);
    }
}
