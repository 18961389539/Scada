using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ReportRunConfiguration : IEntityTypeConfiguration<ReportRun>
{
    public void Configure(EntityTypeBuilder<ReportRun> entity)
    {
        entity.ToTable("ReportRuns");
        entity.HasIndex(x => new { x.TemplateId, x.PeriodKey, x.Version }).IsUnique();
        entity.HasIndex(x => x.GeneratedTs);
        entity.Property(x => x.SignerThumbprint).HasMaxLength(64); entity.Property(x => x.SignerSubject).HasMaxLength(300); entity.Property(x => x.SignatureError).HasMaxLength(500);
    }
}
