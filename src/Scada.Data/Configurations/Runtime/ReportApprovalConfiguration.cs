using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ReportApprovalConfiguration : IEntityTypeConfiguration<ReportApproval>
{
    public void Configure(EntityTypeBuilder<ReportApproval> entity)
    {
        entity.ToTable("ReportApprovals");
        entity.HasIndex(x => new { x.RunId, x.Ts });
        entity.Property(x => x.Step).HasMaxLength(16); entity.Property(x => x.Decision).HasMaxLength(16);
        entity.Property(x => x.UserName).HasMaxLength(100); entity.Property(x => x.DisplayName).HasMaxLength(100); entity.Property(x => x.Reason).HasMaxLength(500);
        entity.Property(x => x.SignerThumbprint).HasMaxLength(64);
        entity.HasOne<ReportRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
    }
}
