using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;

namespace Scada.Data.Configurations.Runtime;

public sealed class ProductionBatchConfiguration : IEntityTypeConfiguration<ProductionBatch>
{
    public void Configure(EntityTypeBuilder<ProductionBatch> entity)
    {
        entity.ToTable("ProductionBatches");
        entity.HasIndex(x => x.BatchNumber).IsUnique();
        entity.HasIndex(x => x.Status);
        entity.HasIndex(x => x.StartedTs);
        entity.Property(x => x.BatchNumber).HasMaxLength(100);
        entity.Property(x => x.RecipeName).HasMaxLength(100);
        entity.Property(x => x.SequenceName).HasMaxLength(100);
        entity.Property(x => x.StartedBy).HasMaxLength(100);
        entity.Property(x => x.Message).HasMaxLength(1000);
    }
}
