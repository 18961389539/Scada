using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class ExternalApiKeyConfiguration : IEntityTypeConfiguration<ExternalApiKey>
{
    public void Configure(EntityTypeBuilder<ExternalApiKey> entity)
    {
        entity.ToTable("ExternalApiKey");
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.Property(x => x.KeyPrefix).IsRequired().HasMaxLength(32);
        entity.Property(x => x.KeyHash).IsRequired().HasMaxLength(64);
        entity.HasIndex(x => x.KeyHash).IsUnique();
        entity.HasIndex(x => x.Name);
        entity.Property(x => x.CreatedBy).HasMaxLength(100);
        entity.Property(x => x.Notes).HasMaxLength(500);
        entity.Property(x => x.AreaIdsJson).HasMaxLength(2000);
    }
}
