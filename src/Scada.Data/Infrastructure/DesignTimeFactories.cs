using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Scada.Data.Infrastructure;

public sealed class ConfigDbContextFactory : IDesignTimeDbContextFactory<ConfigDbContext>
{
    public ConfigDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ConfigDbContext>()
            .UseSqlite("Data Source=scada.db")
            .Options;
        return new ConfigDbContext(options);
    }
}

public sealed class RuntimeDbContextFactory : IDesignTimeDbContextFactory<RuntimeDbContext>
{
    public RuntimeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite("Data Source=runtime.db")
            .Options;
        return new RuntimeDbContext(options);
    }
}

public sealed class HistoryDbContextDesignTimeFactory : IDesignTimeDbContextFactory<HistoryDbContext>
{
    public HistoryDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<HistoryDbContext>()
            .UseSqlite("Data Source=history.db")
            .Options;
        return new HistoryDbContext(options);
    }
}
