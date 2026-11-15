using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class GatewayControlCommandConfiguration : IEntityTypeConfiguration<GatewayControlCommand>
{
    public void Configure(EntityTypeBuilder<GatewayControlCommand> entity)
    {
        entity.ToTable("GatewayCommand");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Revision).HasMaxLength(64).IsRequired();
        entity.Property(x => x.State).HasMaxLength(16).IsRequired();
        entity.Property(x => x.UserName).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Text).HasMaxLength(4000);
        entity.Property(x => x.ExecutionTokenHash).HasMaxLength(64);
        entity.Property(x => x.ReceiptHash).HasMaxLength(64);
        entity.HasIndex(x => new { x.GatewayId, x.SessionId, x.State, x.ExpiresTs });
    }
}
