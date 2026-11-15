using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 RuntimeDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class OperationLogConfiguration : IEntityTypeConfiguration<OperationLog>
{
    public void Configure(EntityTypeBuilder<OperationLog> entity)
    {
        entity.ToTable("OperationLog");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.UserName).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Action).IsRequired().HasMaxLength(200);
        entity.Property(x => x.Result).HasMaxLength(500);
        entity.Property(x => x.TargetType).HasMaxLength(50);
        entity.Property(x => x.TargetId).HasMaxLength(100);
        entity.HasIndex(x => x.Ts);
        // 审计发件箱投递的幂等键：同一发件箱行重复投递（崩溃后重试）不会写出第二条审计。
        entity.HasIndex(x => x.SourceId).IsUnique().HasFilter("\"SourceId\" IS NOT NULL");
    }
}
