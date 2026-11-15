using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class RecipeVersionConfiguration : IEntityTypeConfiguration<RecipeVersion>
{
    public void Configure(EntityTypeBuilder<RecipeVersion> entity)
    {
        entity.HasIndex(x => new { x.RecipeId, x.Version }).IsUnique();
        entity.Property(x => x.Note).HasMaxLength(200); entity.Property(x => x.Source).HasMaxLength(16); entity.Property(x => x.CreatedBy).HasMaxLength(100);
        entity.HasOne<Recipe>().WithMany().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
    }
}
