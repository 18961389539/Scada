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
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        var options = new DbContextOptionsBuilder<HistoryDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;

        var context = new HistoryDbContext(options);
        EnsureCreated(context);
        return context;
    }
}
