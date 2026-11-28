using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;

namespace Scada.Data.Configurations.Runtime;

public sealed class DeviceAcquisitionStateConfiguration : IEntityTypeConfiguration<DeviceAcquisitionState>
{
    public void Configure(EntityTypeBuilder<DeviceAcquisitionState> entity)
    {
        entity.ToTable("DeviceAcquisitionStates");
        entity.HasKey(x => x.DeviceId);
        entity.Property(x => x.ChangedBy).HasMaxLength(100);
        entity.Property(x => x.Reason).HasMaxLength(1000);
        entity.HasIndex(x => x.ActivePath);
    }
}
