using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Options;

namespace Scada.Data.Infrastructure;

public interface IHistoryDbFactory
{
    HistoryDbContext CreateRaw(int year, int month);
    HistoryDbContext CreateAggregate(int year);
    string GetRawPath(int year, int month);
    string GetAggregatePath(int year);
    void EnsureCreated(HistoryDbContext context);
    void ReleaseFile(string path) { }
}

public sealed class HistoryDbFactory : IHistoryDbFactory
{
    private readonly ScadaSettings _settings;
    private readonly ConcurrentDictionary<string, bool> _initialized = new(StringComparer.OrdinalIgnoreCase);

    public HistoryDbFactory(IOptions<ScadaSettings> settings)
    {
        _settings = settings.Value;
        Directory.CreateDirectory(_settings.HistoryRoot);
    }

    public string GetRawPath(int year, int month) =>
        Path.Combine(_settings.HistoryRoot, $"raw_{year}{month:00}.db");

    public string GetAggregatePath(int year) =>
        Path.Combine(_settings.HistoryRoot, $"agg_{year}.db");

    public HistoryDbContext CreateRaw(int year, int month) => Create(GetRawPath(year, month));

    public HistoryDbContext CreateAggregate(int year) => Create(GetAggregatePath(year));

    public void ReleaseFile(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        SqliteConnection.ClearPool(connection);
        _initialized.TryRemove(path, out _);
    }

    public void EnsureCreated(HistoryDbContext context)
    {
        var path = context.Database.GetDbConnection().DataSource;
        if (_initialized.ContainsKey(path))
        {
            return;
        }

        context.Database.Migrate();
        _initialized.TryAdd(path, true);
    }

    private HistoryDbContext Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var options = new DbContextOptionsBuilder<HistoryDbContext>()
            .UseSqlite(ConnectionString(path))
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;

        var context = new HistoryDbContext(options);
        EnsureCreated(context);
        return context;
    }

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // WAL readers must not take shared-cache table locks that block the archive writer.
        Cache = SqliteCacheMode.Private,
        Pooling = true
    }.ToString();
}
