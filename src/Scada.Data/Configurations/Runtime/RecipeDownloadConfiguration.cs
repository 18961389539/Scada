using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class RecipeDownloadConfiguration : IEntityTypeConfiguration<RecipeDownload>
{
    public void Configure(EntityTypeBuilder<RecipeDownload> entity)
    {
        entity.ToTable("RecipeDownloads");
        entity.HasIndex(x => new { x.RecipeId, x.CreatedTs }); entity.HasIndex(x => x.Status);
        entity.Property(x => x.RecipeName).HasMaxLength(100); entity.Property(x => x.RequestedBy).HasMaxLength(100);
        entity.Property(x => x.ApprovedBy).HasMaxLength(100); entity.Property(x => x.Message).HasMaxLength(1000);
    }
}
