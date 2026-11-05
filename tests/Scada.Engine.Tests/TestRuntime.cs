using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scada.Data;

namespace Scada.Engine.Tests;

internal static class TestRuntime
{
    public static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    public static IDbContextFactory<RuntimeDbContext> Factory(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite(connection).Options;
        using (var db = new RuntimeDbContext(options)) db.Database.Migrate();
        return new RuntimeFactory(options);
    }

    private sealed class RuntimeFactory(DbContextOptions<RuntimeDbContext> options) : IDbContextFactory<RuntimeDbContext>
    {
        public RuntimeDbContext CreateDbContext() => new(options);
        public Task<RuntimeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
