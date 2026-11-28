using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Scada.Core.Entities;

namespace Scada.Data.Configurations.Runtime;

public sealed class KioskMonitorSessionConfiguration : IEntityTypeConfiguration<KioskMonitorSession>
{
    public void Configure(EntityTypeBuilder<KioskMonitorSession> entity)
    {
        entity.ToTable("KioskMonitorSessions");
        entity.HasKey(x => x.SessionId);
        entity.Property(x => x.SessionId).HasMaxLength(64);
        entity.HasIndex(x => x.UserId);
        entity.HasIndex(x => x.ExpiresTs);
    }
}
