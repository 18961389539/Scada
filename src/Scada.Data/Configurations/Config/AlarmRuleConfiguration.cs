using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class AlarmRuleConfiguration : IEntityTypeConfiguration<AlarmRule>
{
    public void Configure(EntityTypeBuilder<AlarmRule> entity)
    {
        entity.ToTable("AlarmRule");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Message).HasMaxLength(500);
        entity.Property(x => x.MessageEn).HasMaxLength(500);
        entity.Property(x => x.Cause).HasMaxLength(200);
        entity.Property(x => x.CauseEn).HasMaxLength(200);
        entity.Property(x => x.Action).HasMaxLength(200);
        entity.Property(x => x.ActionEn).HasMaxLength(200);
        entity.Property(x => x.Expression).HasMaxLength(4096);
        entity.Property(x => x.OutOfServiceReason).HasMaxLength(500);
        // 位报警触发条件只对 Discrete 有意义，旧数据统一按 On 落库；StateChange 在评估时按 Change 处理。
        entity.Property(x => x.ActiveWhen).HasDefaultValue(Scada.Core.Enums.AlarmActiveWhen.On);
        entity.Property(x => x.Category).HasDefaultValue(Scada.Core.Enums.AlarmCategory.Process);
        entity.Property(x => x.AutoPriority).HasDefaultValue(false);
        entity.HasIndex(x => new { x.TagId, x.Type });
        entity.Property(x => x.Key).HasMaxLength(64).HasDefaultValue("");
        entity.HasIndex(x => new { x.TagId, x.Key }).IsUnique();
        entity.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
    }
}
