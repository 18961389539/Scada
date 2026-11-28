using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;

namespace Scada.Data.Configurations.Config;

public sealed class KioskPlaylistConfiguration : IEntityTypeConfiguration<KioskPlaylist>
{
    public void Configure(EntityTypeBuilder<KioskPlaylist> entity)
    {
        entity.ToTable("KioskPlaylist");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.Property(x => x.ScreenIdsJson).IsRequired().HasMaxLength(4000);
        entity.Property(x => x.UpdatedBy).IsRequired().HasMaxLength(100);
        entity.HasIndex(x => x.Name).IsUnique();
    }
}
