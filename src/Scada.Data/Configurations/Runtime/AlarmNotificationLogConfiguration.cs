using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class AlarmNotificationLogConfiguration : IEntityTypeConfiguration<AlarmNotificationLog>
{
    public void Configure(EntityTypeBuilder<AlarmNotificationLog> entity)
    {
        entity.ToTable("AlarmNotificationLog");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Action).IsRequired().HasMaxLength(40);
        entity.Property(x => x.Error).HasMaxLength(500);
        entity.HasIndex(x => new { x.AlarmEventId, x.RouteId, x.Action });
        entity.HasOne<AlarmEvent>().WithMany().HasForeignKey(x => x.AlarmEventId).OnDelete(DeleteBehavior.Cascade);
    }
}
