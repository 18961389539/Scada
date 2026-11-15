using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> entity)
    {
        entity.HasIndex(x => new { x.TemplateId, x.Name }).IsUnique();
        entity.Property(x => x.Name).HasMaxLength(100).IsRequired(); entity.Property(x => x.Description).HasMaxLength(500);
        entity.Property(x => x.UpdatedBy).HasMaxLength(100); entity.Property(x => x.CurrentVersion).IsConcurrencyToken();
        entity.HasOne<RecipeTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
    }
}
