using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;

namespace Scada.Data.Configurations.Config;

public sealed class SiteOperationsSettingsConfiguration : IEntityTypeConfiguration<SiteOperationsSettings>
{
    public void Configure(EntityTypeBuilder<SiteOperationsSettings> entity)
    {
        entity.ToTable("SiteOperationsSettings", table => table.HasCheckConstraint("CK_SiteOperationsSettings_Singleton", "Id = 1"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.ShiftTimeZone).HasMaxLength(100);
        entity.Property(x => x.UpdatedBy).HasMaxLength(100);
    }
}
