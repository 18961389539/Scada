using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class AuditOutboxEntryConfiguration : IEntityTypeConfiguration<AuditOutboxEntry>
{
    public void Configure(EntityTypeBuilder<AuditOutboxEntry> entity)
    {
        entity.ToTable("AuditOutbox");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Payload).IsRequired();
        entity.Property(x => x.LastError).HasMaxLength(500);
        // 投递按 NextAttemptTs 取待办，保留期清理按 CreatedTs。
        entity.HasIndex(x => x.NextAttemptTs);
        entity.HasIndex(x => x.CreatedTs);
    }
}
