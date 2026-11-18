using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data;
using Scada.Tests;

namespace Scada.Server.Tests;

internal static class TestDatabaseTemplate
{
    private static readonly Lazy<Task<string>> Root = new(CreateAsync);

    public static async Task CopyToAsync(string targetRoot)
    {
        var source = await Root.Value.WaitAsync(TimeSpan.FromMinutes(2));
        foreach (var name in new[] { "config.db", "runtime.db" })
            File.Copy(Path.Combine(source, name), Path.Combine(targetRoot, name), overwrite: true);
    }

    private static async Task<string> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "scada-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TempCleanup.Track(root);
        var settings = new ScadaSettings
        {
            ConfigDbPath = Path.Combine(root, "config.db"),
            RuntimeDbPath = Path.Combine(root, "runtime.db"),
            HistoryRoot = Path.Combine(root, "history"),
            BackupRoot = Path.Combine(root, "backups"),
            DiagnosticsRoot = Path.Combine(root, "diag"),
            ReportsRoot = Path.Combine(root, "reports"),
            SeedDemoData = false,
            ReportSchedulerEnabled = false,
            RequirePasswordChangeOnFirstLogin = false,
            FullBackupIntervalHours = 0
        };
        var services = new ServiceCollection()
            .AddLogging()
            .AddDbContextFactory<ConfigDbContext>(options => options.UseSqlite($"Data Source={settings.ConfigDbPath};Pooling=False"))
            .AddDbContextFactory<RuntimeDbContext>(options => options.UseSqlite($"Data Source={settings.RuntimeDbPath};Pooling=False"))
            .AddSingleton<IOptions<ScadaSettings>>(Options.Create(settings))
            .AddSingleton<Scada.Core.Interfaces.IConfigSnapshot>(provider =>
                new ConfigSnapshot(provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>()));
        await using (var provider = services.BuildServiceProvider())
            await DatabaseInitializer.InitializeAsync(provider);
        Checkpoint(settings.ConfigDbPath);
        Checkpoint(settings.RuntimeDbPath);
        return root;
    }

    private static void Checkpoint(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
    }
}