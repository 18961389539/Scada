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
    public async Task ScreenEraDatabaseUpgradePreservesPublishedScreensAndAddsUsableObjectTemplates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection);
        int deviceId, tagId, screenId, templateId;
        var originalDocument = ObjectTemplateJson.Write(new ScreenDocument());
        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261023172519_AddProcessScreens");
            var device = new Device { Name = "legacy-plc", Enabled = false }; db.Devices.Add(device); await db.SaveChangesAsync(); deviceId = device.Id;
            var tag = new Tag { DeviceId = deviceId, Name = "legacy-tag", Scale = .1, Offset = 10 }; db.Tags.Add(tag);
            var screen = new ProcessScreen { Name = "legacy-screen", DraftJson = originalDocument, PublishedJson = originalDocument, PublishedName = "legacy-screen", PublishedRevision = 1, UpdatedBy = "test" }; db.ProcessScreens.Add(screen);
            await db.SaveChangesAsync(); tagId = tag.Id; screenId = screen.Id;
            await db.Database.MigrateAsync();
            var template = new DeviceObjectTemplate { Name = "migrated-pump", DraftJson = ObjectTemplateJson.Write(ObjectTemplateExamples.Pump()), LatestVersion = 1, UpdatedBy = "test" };
            db.ObjectTemplates.Add(template); await db.SaveChangesAsync(); templateId = template.Id;
            db.ObjectTemplateVersions.Add(new DeviceObjectTemplateVersion { TemplateId = templateId, Version = 1, DocumentJson = template.DraftJson, CreatedBy = "test" }); await db.SaveChangesAsync();
        }
        var service = new Scada.Engine.Templates.ObjectTemplateService(factory);
        await service.CreateAsync(templateId, new(1, [new("P01", deviceId, 100)]), null, "test", default);
        await using var result = factory.CreateDbContext();
        Assert.Equal(originalDocument, (await result.ProcessScreens.FindAsync(screenId))!.PublishedJson);
        Assert.Equal(.1, (await result.Tags.FindAsync(tagId))!.Scale); Assert.Equal(10, (await result.Tags.FindAsync(tagId))!.Offset);
        Assert.Single(await result.ObjectInstances.ToListAsync());
    }

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
