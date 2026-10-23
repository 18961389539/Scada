using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;

namespace Scada.Engine.Tests;

public class DatabaseUpgradeTests
{
    [Fact]
    public async Task ExistingDatabaseUpgrade_PreservesDevicesAndAddsScreens()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection);
        await using var db = factory.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261023161840_InitialCreate");
        db.Devices.Add(new Device { Name = "existing-device" }); await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.Equal("existing-device", (await db.Devices.SingleAsync()).Name);
        db.ProcessScreens.Add(new ProcessScreen { Name = "new-screen", DraftJson = "{}", UpdatedBy = "test" });
        await db.SaveChangesAsync(); Assert.Single(await db.ProcessScreens.ToListAsync());
    }

    [Fact]
    public async Task NewDemoDatabase_HasValidEditablePublishedScreenAndNonOverlappingRegisters()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection);
        var root = Path.Combine(Path.GetTempPath(), "scada-seed-test-" + Guid.NewGuid().ToString("N"));
        using var services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<ConfigDbContext>>(factory)
            .AddSingleton<IConfigSnapshot, ConfigSnapshot>()
            .AddSingleton<IOptions<ScadaSettings>>(Options.Create(new ScadaSettings
            {
                SeedDemoData = true, ConfigDbPath = Path.Combine(root, "config.db"), HistoryRoot = Path.Combine(root, "history"), BackupRoot = Path.Combine(root, "backups")
            })).BuildServiceProvider();
        try
        {
            await DatabaseInitializer.InitializeAsync(services);
            await using var db = factory.CreateDbContext();
            var screen = await db.ProcessScreens.SingleAsync();
            Assert.Equal(screen.DraftJson, screen.PublishedJson); Assert.Equal(screen.Revision, screen.PublishedRevision);
            var document = JsonSerializer.Deserialize<ScreenDocument>(screen.PublishedJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id);
            Assert.Empty(ScreenValidator.Validate(document, tags, new HashSet<int> { screen.Id }));
            Assert.Contains(document.Nodes, x => x.Type == "pump" && x.TagId is not null);
            var registerAddresses = tags.Values.Where(t => t.Area == Core.Enums.TagArea.HoldingRegister)
                .SelectMany(t => Enumerable.Range(t.Address, t.RegisterCount)).ToList();
            Assert.Equal(registerAddresses.Count, registerAddresses.Distinct().Count());
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("scada-seed-test-", StringComparison.Ordinal)) Directory.Delete(full, true);
        }
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }
}
