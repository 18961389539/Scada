using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class TagDependencyConfiguration : IEntityTypeConfiguration<TagDependency>
{
    public void Configure(EntityTypeBuilder<TagDependency> entity)
    {
        entity.ToTable("TagDependency"); entity.HasKey(x => new { x.TagId, x.InputTagId });
        entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.InputTagId).OnDelete(DeleteBehavior.Restrict);
    }
}
