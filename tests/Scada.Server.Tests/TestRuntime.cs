using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scada.Data;

namespace Scada.Server.Tests;

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
        return new ContextFactory(options);
    }

    public static IDbContextFactory<RuntimeDbContext> Factory(string path)
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite($"Data Source={path}").Options;
        using (var db = new RuntimeDbContext(options)) db.Database.Migrate();
        return new ContextFactory(options);
    }

    private sealed class ContextFactory(DbContextOptions<RuntimeDbContext> options) : IDbContextFactory<RuntimeDbContext>
    {
        public RuntimeDbContext CreateDbContext() => new(options);
        public Task<RuntimeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
