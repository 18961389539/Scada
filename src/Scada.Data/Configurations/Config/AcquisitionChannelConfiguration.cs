using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class AcquisitionChannelConfiguration : IEntityTypeConfiguration<AcquisitionChannel>
{
    public void Configure(EntityTypeBuilder<AcquisitionChannel> entity)
    {
        entity.ToTable("Channel");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.HasIndex(x => x.Name).IsUnique();
        entity.Property(x => x.Description).HasMaxLength(500).HasDefaultValue("");
        entity.Property(x => x.Host).HasMaxLength(255).HasDefaultValue("127.0.0.1");
        entity.Property(x => x.SerialPort).HasMaxLength(128).HasDefaultValue("COM1");
        entity.Property(x => x.BaudRate).HasDefaultValue(9600);
        entity.Property(x => x.DataBits).HasDefaultValue(8);
        entity.Property(x => x.StopBits).HasDefaultValue(Scada.Core.Enums.SerialStopBits.One);
        entity.Property(x => x.TimeoutMs).HasDefaultValue(3000);
        // 通道删除前必须由接口清空引用；这里用 Restrict 兜底，避免静默把设备变成直连。
        entity.HasMany<Device>().WithOne().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(x => x.Revision).HasDefaultValue(1L).IsConcurrencyToken();
    }
}
