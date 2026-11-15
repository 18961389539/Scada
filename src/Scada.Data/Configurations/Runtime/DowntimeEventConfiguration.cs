using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class DowntimeEventConfiguration : IEntityTypeConfiguration<DowntimeEvent>
{
    public void Configure(EntityTypeBuilder<DowntimeEvent> entity)
    {
        entity.ToTable("DowntimeEvent");
        entity.Property(x => x.Source).IsRequired().HasMaxLength(20);
        entity.Property(x => x.Note).HasMaxLength(500);
        entity.Property(x => x.CreatedBy).IsRequired().HasMaxLength(100);
        entity.Property(x => x.UpdatedBy).HasMaxLength(100);
        entity.HasIndex(x => new { x.LineId, x.StartTs });
        entity.HasIndex(x => new { x.LineId, x.EndTs });
        entity.HasIndex(x => x.AlarmEventId);
    }
}
