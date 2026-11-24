using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Config;

// 由 OnModelCreating 机械拆出：一个实体一个配置类，避免多人同时编辑同一个巨型方法。
// 加载方式见 ConfigDbContext.OnModelCreating 的 ApplyConfigurationsFromAssembly。
public sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> entity)
    {
        entity.ToTable("Device");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.HasIndex(x => x.Name).IsUnique();
        entity.Property(x => x.SerialPort).HasMaxLength(128).HasDefaultValue("COM1");
        entity.HasOne<AcquisitionGateway>().WithMany().HasForeignKey(x => x.GatewayId).OnDelete(DeleteBehavior.Restrict);
        // 区域引用不建数据库外键：旧库升级只需追加列、不重建设备表；删除区域前由接口检查无设备引用。
        entity.Property(x => x.AreaId).HasDefaultValue(Area.DefaultId).HasSentinel(0);
        entity.HasIndex(x => x.AreaId);
        entity.Property(x => x.BaudRate).HasDefaultValue(9600);
        entity.Property(x => x.DataBits).HasDefaultValue(8);
        entity.Property(x => x.StopBits).HasDefaultValue(Scada.Core.Enums.SerialStopBits.One);
        entity.Property(x => x.Cpu).HasDefaultValue(Scada.Core.Enums.S7Cpu.S71200).HasSentinel((Scada.Core.Enums.S7Cpu)(-1));
        entity.Property(x => x.Slot).HasDefaultValue((short)1).HasSentinel((short)-1);
        entity.Property(x => x.OpcEndpointUrl).HasMaxLength(1024).HasDefaultValue("opc.tcp://localhost:4840");
        entity.Property(x => x.OpcSecurityMode).HasDefaultValue(Scada.Core.Enums.OpcSecurityMode.SignAndEncrypt).HasSentinel((Scada.Core.Enums.OpcSecurityMode)(-1));
        entity.Property(x => x.OpcUserName).HasMaxLength(200).HasDefaultValue("");
        entity.Property(x => x.ProtocolOptions).HasMaxLength(16000).HasDefaultValue("");
        entity.Property(x => x.SimulationOptions).HasMaxLength(16000).HasDefaultValue("");
        entity.HasIndex(x => x.ChannelId);
        entity.Property(x => x.Revision).HasDefaultValue(1L).IsConcurrencyToken();
    }
}
