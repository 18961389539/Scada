using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class AlarmEventConfiguration : IEntityTypeConfiguration<AlarmEvent>
{
    public void Configure(EntityTypeBuilder<AlarmEvent> entity)
    {
        entity.ToTable("AlarmEvent");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Message).IsRequired().HasMaxLength(500);
        entity.Property(x => x.MessageEn).HasMaxLength(500);
        entity.Property(x => x.AckUser).HasMaxLength(100);
        entity.Property(x => x.EndReason).HasMaxLength(100);
        entity.Property(x => x.SuppressReason).HasMaxLength(200);
        entity.HasIndex(x => x.RuleId);
        entity.HasIndex(x => new { x.DeviceId, x.GatewayId }).IsUnique()
            .HasFilter("\"GatewayId\" IS NOT NULL AND \"DeviceId\" IS NOT NULL AND \"RuleId\" IS NULL AND \"TagId\" IS NULL AND \"EndTs\" IS NULL");
        entity.HasIndex(x => x.StartTs);
        entity.HasIndex(x => new { x.Category, x.StartTs });
        entity.HasIndex(x => new { x.TagId, x.StartTs });
        entity.HasIndex(x => new { x.RuleId, x.StartTs });
        entity.HasIndex(x => new { x.EndTs, x.StartTs });
        entity.HasIndex(x => x.EndTs);
    }
}
