using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class CalcJobPublicationConfiguration : IEntityTypeConfiguration<CalcJobPublication>
{
    public void Configure(EntityTypeBuilder<CalcJobPublication> entity)
    {
        entity.ToTable("CalcJobPublications");
        entity.HasIndex(x => x.JobId);
        entity.Property(x => x.UserName).HasMaxLength(100);
        entity.HasOne<CalcJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}
