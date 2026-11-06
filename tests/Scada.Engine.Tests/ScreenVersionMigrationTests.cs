using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;

namespace Scada.Engine.Tests;

public sealed class ScreenVersionMigrationTests
{
    private const string PreviousMigration = "20261030074235_AddAlarmRuleGuidance";

    [Fact]
    public async Task UpgradeBackfillsOnlyTheCurrentPublicationWithoutChangingAnyScreenBytes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var factory = new Factory(connection);
        ProcessScreen[] originals;
        await using (var legacy = factory.CreateDbContext())
        {
            await legacy.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            originals =
            [
                new() { Name = "已改名的草稿", PublishedName = "当时发布的名称", Revision = 12, PublishedRevision = 7,
                    DraftJson = "{ \"schemaVersion\": 1, \"nodes\": [] }",
                    PublishedJson = "{\"schemaVersion\":1,\"nodes\":[{\"id\":\"legacy\",\"type\":\"template\",\"templateKey\":\"old\"}]}",
                    UpdatedTs = 123456789, UpdatedBy = "legacy-editor" },
                new() { Name = "旧版本名称回退", PublishedName = null, Revision = 8, PublishedRevision = 4,
                    DraftJson = "{\"draft\":true}", PublishedJson = "{ \"nodes\": [] }", UpdatedTs = 234567890, UpdatedBy = "legacy-admin" },
                new() { Name = "仅草稿", DraftJson = "{ \"draftOnly\": true }", Revision = 6, UpdatedTs = 345678901, UpdatedBy = "draft-editor" },
                new() { Name = "没有发布号", DraftJson = "{}", PublishedJson = "{\"partial\":true}", Revision = 3, UpdatedBy = "partial" },
                new() { Name = "没有发布文档", DraftJson = "{}", PublishedRevision = 2, Revision = 3, UpdatedBy = "partial" }
            ];
            // 旧库没有 DraftDirty 列，直接用 SQL 写入画面，避免当前实体模型的新列。
            foreach (var screen in originals)
                await legacy.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ProcessScreen (Name, DraftJson, PublishedJson, PublishedName, Revision, PublishedRevision, UpdatedTs, UpdatedBy) VALUES ({screen.Name}, {screen.DraftJson}, {screen.PublishedJson}, {screen.PublishedName}, {screen.Revision}, {screen.PublishedRevision}, {screen.UpdatedTs}, {screen.UpdatedBy})");
            var ids = await legacy.ProcessScreens.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
            for (var i = 0; i < originals.Length; i++) originals[i].Id = ids[i];
        }

        var originalBytes = JsonSerializer.Serialize(originals.OrderBy(x => x.Id).ToArray());
        string historyBytes;
        await using (var upgraded = factory.CreateDbContext())
        {
            await upgraded.Database.MigrateAsync();
            Assert.Contains(await upgraded.Database.GetAppliedMigrationsAsync(), id => id.EndsWith("_ScreenPublicationHistory", StringComparison.Ordinal));
            Assert.False(upgraded.Database.HasPendingModelChanges());
            var screens = await upgraded.ProcessScreens.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
            Assert.Equal(originalBytes, JsonSerializer.Serialize(screens));
            var history = await upgraded.ProcessScreenVersions.AsNoTracking().OrderBy(x => x.ScreenId).ToArrayAsync();
            Assert.Equal(2, history.Length);
            foreach (var original in originals.Where(x => x.PublishedJson is not null && x.PublishedRevision is not null))
            {
                var version = Assert.Single(history, x => x.ScreenId == original.Id);
                Assert.True(version.Id > 0);
                Assert.Equal(original.PublishedRevision, version.Revision);
                Assert.Equal(original.PublishedName ?? original.Name, version.Name);
                Assert.Equal(original.PublishedJson, version.DocumentJson);
                Assert.Equal(original.UpdatedTs, version.PublishedTs);
                Assert.Equal(original.UpdatedBy, version.PublishedBy);
                Assert.Equal("migration", version.Kind);
                Assert.Contains("补录", version.Note);
                Assert.Null(version.RestoredFromRevision);
            }
            historyBytes = JsonSerializer.Serialize(history);
        }

        await using var reopened = factory.CreateDbContext();
        await reopened.Database.MigrateAsync();
        Assert.Equal(historyBytes, JsonSerializer.Serialize(await reopened.ProcessScreenVersions.AsNoTracking().OrderBy(x => x.ScreenId).ToArrayAsync()));
        Assert.Equal(originalBytes, JsonSerializer.Serialize(await reopened.ProcessScreens.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
        Assert.False(reopened.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task MigratedHistoryEnforcesPerScreenRevisionUniquenessAndCascadingForeignKeys()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var factory = new Factory(connection);
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        var first = new ProcessScreen { Name = "first", UpdatedBy = "test" };
        var second = new ProcessScreen { Name = "second", UpdatedBy = "test" };
        db.ProcessScreens.AddRange(first, second);
        await db.SaveChangesAsync();
        ProcessScreenVersion Version(int screenId) => new()
        {
            ScreenId = screenId, Revision = 3, Name = "frozen", DocumentJson = "{}",
            PublishedTs = 1000, PublishedBy = "test", Note = "初始发布", Kind = "publish"
        };
        db.ProcessScreenVersions.AddRange(Version(first.Id), Version(second.Id));
        await db.SaveChangesAsync();

        db.ProcessScreenVersions.Add(Version(first.Id));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(2067, Assert.IsType<SqliteException>(duplicate.InnerException).SqliteExtendedErrorCode);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.ProcessScreenVersions.CountAsync());

        db.ProcessScreenVersions.Add(Version(int.MaxValue));
        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(787, Assert.IsType<SqliteException>(orphan.InnerException).SqliteExtendedErrorCode);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.ProcessScreenVersions.CountAsync());

        Assert.Equal(1, await db.ProcessScreens.Where(x => x.Id == first.Id).ExecuteDeleteAsync());
        var survivor = await db.ProcessScreenVersions.AsNoTracking().SingleAsync();
        Assert.Equal(second.Id, survivor.ScreenId);
        Assert.Null(survivor.RestoredFromRevision);
    }

    [Fact]
    public async Task DemoInitializationCreatesOneMatchingHistoryEntryPerPublicationAndDoesNotDuplicateIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var factory = new Factory(connection);
        var root = Path.Combine(Directory.GetCurrentDirectory(), "screen-version-seed-" + Guid.NewGuid().ToString("N"));
        using var services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<ConfigDbContext>>(factory)
            // DatabaseInitializer 会先迁移运行库：缺这个工厂时整个初始化直接抛，测试根本没跑到位。
            .AddSingleton<IDbContextFactory<RuntimeDbContext>>(new RuntimeFactory(Path.Combine(root, "runtime.db")))
            .AddSingleton<IConfigSnapshot, ConfigSnapshot>()
            .AddSingleton<IOptions<ScadaSettings>>(Options.Create(new ScadaSettings
            {
                SeedDemoData = true, ConfigDbPath = Path.Combine(root, "config.db"),
                HistoryRoot = Path.Combine(root, "history"), BackupRoot = Path.Combine(root, "backups")
            })).BuildServiceProvider();
        try
        {
            await using (var existing = factory.CreateDbContext())
            {
                await existing.Database.MigrateAsync();
                var screen = new ProcessScreen
                {
                    Name = "existing", PublishedName = "existing", DraftJson = "{}", PublishedJson = "{}",
                    PublishedRevision = 1, UpdatedTs = 1000, UpdatedBy = "existing-publisher"
                };
                existing.ProcessScreens.Add(screen);
                await existing.SaveChangesAsync();
                ProcessScreenHistory.Record(existing, screen, 1000, "existing-publisher", "Existing publication", "publish");
                await existing.SaveChangesAsync();
            }
            await DatabaseInitializer.InitializeAsync(services);
            string historyBytes;
            await using (var db = factory.CreateDbContext())
            {
                var screens = await db.ProcessScreens.AsNoTracking().Where(x => x.PublishedJson != null && x.PublishedRevision != null).ToArrayAsync();
                Assert.NotEmpty(screens);
                var versions = await db.ProcessScreenVersions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync();
                Assert.Equal(screens.Length, versions.Length);
                foreach (var screen in screens)
                {
                    var version = Assert.Single(versions, x => x.ScreenId == screen.Id);
                    Assert.Equal(screen.PublishedRevision, version.Revision);
                    Assert.Equal(screen.PublishedName ?? screen.Name, version.Name);
                    Assert.Equal(screen.PublishedJson, version.DocumentJson);
                    Assert.True(version.PublishedTs > 0);
                    Assert.False(string.IsNullOrWhiteSpace(version.PublishedBy));
                    Assert.Null(version.RestoredFromRevision);
                }
                historyBytes = JsonSerializer.Serialize(versions);
            }
            await DatabaseInitializer.InitializeAsync(services);
            await using var reopened = factory.CreateDbContext();
            Assert.Equal(historyBytes, JsonSerializer.Serialize(await reopened.ProcessScreenVersions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync()));
            Assert.False(reopened.Database.HasPendingModelChanges());
        }
        finally
        {
            // 运行库连接池会持有文件句柄，不清池在 Windows 上删不掉目录。
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }

    private sealed class RuntimeFactory(string path) : IDbContextFactory<RuntimeDbContext>
    {
        public RuntimeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite($"Data Source={path}").Options);
    }
}
