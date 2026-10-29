using Microsoft.EntityFrameworkCore;

namespace Scada.Data;

public class HistoryValue
{
    public int TagId { get; set; }
    public long Ts { get; set; }
    public double Value { get; set; }
    public int Quality { get; set; }
}

public class Agg5Min
{
    public int TagId { get; set; }
    public long Ts { get; set; }
    public double Avg { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public int Count { get; set; }
}

public class AggHour
{
    public int TagId { get; set; }
    public long Ts { get; set; }
    public double Avg { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public int Count { get; set; }
}

public class PendingAggregation
{
    public int WindowMs { get; set; }
    public long Ts { get; set; }
    public long Revision { get; set; }
}

public class HistoryDbContext : DbContext
{
    public HistoryDbContext(DbContextOptions<HistoryDbContext> options) : base(options)
    {
    }

    public DbSet<HistoryValue> HistoryValues => Set<HistoryValue>();
    public DbSet<Agg5Min> Agg5Min => Set<Agg5Min>();
    public DbSet<AggHour> AggHour => Set<AggHour>();
    public DbSet<PendingAggregation> PendingAggregations => Set<PendingAggregation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HistoryValue>(entity =>
        {
            entity.ToTable("HistoryValue");
            entity.HasKey(x => new { x.TagId, x.Ts });
            entity.HasIndex(x => x.Ts);
        });

        modelBuilder.Entity<Agg5Min>(entity =>
        {
            entity.ToTable("Agg5Min");
            entity.HasKey(x => new { x.TagId, x.Ts });
            entity.HasIndex(x => x.Ts);
        });

        modelBuilder.Entity<AggHour>(entity =>
        {
            entity.ToTable("AggHour");
            entity.HasKey(x => new { x.TagId, x.Ts });
            entity.HasIndex(x => x.Ts);
        });

        modelBuilder.Entity<PendingAggregation>(entity =>
        {
            entity.ToTable("PendingAggregation");
            entity.HasKey(x => new { x.WindowMs, x.Ts });
        });
    }
}
