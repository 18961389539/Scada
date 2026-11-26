using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;

namespace Scada.Data.Configurations.Runtime;

public sealed class QualityInspectionRevisionConfiguration : IEntityTypeConfiguration<QualityInspectionRevision>
{
    public void Configure(EntityTypeBuilder<QualityInspectionRevision> entity)
    {
        entity.ToTable("QualityInspectionRevision");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.User).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        entity.Property(x => x.Action).HasMaxLength(20).IsRequired();
        entity.HasIndex(x => new { x.InspectionId, x.Ts });
    }
}
