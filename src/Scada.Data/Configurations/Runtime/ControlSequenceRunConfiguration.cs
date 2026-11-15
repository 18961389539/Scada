using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ControlSequenceRunConfiguration : IEntityTypeConfiguration<ControlSequenceRun>
{
    public void Configure(EntityTypeBuilder<ControlSequenceRun> entity)
    {
        entity.ToTable("ControlSequenceRuns");
        entity.HasIndex(x => new { x.SequenceId, x.StartedTs });
        entity.Property(x => x.SequenceName).HasMaxLength(100); entity.Property(x => x.StartedBy).HasMaxLength(100);
        entity.Property(x => x.ApprovedBy).HasMaxLength(100); entity.Property(x => x.Message).HasMaxLength(500);
    }
}
