using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;
using Scada.Data.Infrastructure;

namespace Scada.Data.Configurations.Runtime;

public sealed class MomentaryLeaseConfiguration : IEntityTypeConfiguration<MomentaryLease>
{
    public void Configure(EntityTypeBuilder<MomentaryLease> entity)
    {
        entity.ToTable("MomentaryLeases");
        entity.HasKey(x => x.Id);
        entity.HasIndex(x => x.TagId).IsUnique();
        entity.HasIndex(x => x.ExpiresTs);
        entity.Property(x => x.UserName).HasMaxLength(100);
        entity.Property(x => x.Source).HasMaxLength(200);
    }
}