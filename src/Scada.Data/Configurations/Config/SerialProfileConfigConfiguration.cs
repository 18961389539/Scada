using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class SerialProfileConfigConfiguration : IEntityTypeConfiguration<SerialProfileConfig>
{
    public void Configure(EntityTypeBuilder<SerialProfileConfig> entity)
    {
        entity.ToTable("SerialProfile"); entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(SerialProfileRules.MaxNameLength); entity.HasIndex(x => x.Name).IsUnique();
        entity.Property(x => x.Description).HasMaxLength(500);
        entity.Property(x => x.Port).IsRequired().HasMaxLength(SerialProfileRules.MaxPortLength);
        entity.Property(x => x.Encoding).IsRequired().HasMaxLength(16);
        entity.Property(x => x.NewLine).IsRequired().HasMaxLength(4);
        entity.Property(x => x.Revision).IsConcurrencyToken();
    }
}
