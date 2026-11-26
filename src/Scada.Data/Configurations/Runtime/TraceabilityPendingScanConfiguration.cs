using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;

namespace Scada.Data.Configurations.Runtime;

public sealed class TraceabilityPendingScanConfiguration : IEntityTypeConfiguration<TraceabilityPendingScan>
{
    public void Configure(EntityTypeBuilder<TraceabilityPendingScan> entity)
    {
        entity.ToTable("TraceabilityPendingScan");
        entity.HasKey(x => x.JobId);
        entity.Property(x => x.Sn).HasMaxLength(200).IsRequired();
    }
}
