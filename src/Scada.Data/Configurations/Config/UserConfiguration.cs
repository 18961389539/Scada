using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> entity)
    {
        entity.ToTable("User");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.UserName).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Locale).HasMaxLength(10);
        entity.Property(x => x.PasswordHash).IsRequired().HasMaxLength(500);
        entity.Property(x => x.DisplayName).HasMaxLength(100);
        entity.HasIndex(x => x.UserName).IsUnique();
    }
}
