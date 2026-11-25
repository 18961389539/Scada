using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Calculations;

namespace Scada.Engine.Tests;

public sealed class FactoryCalendarServiceTests
{
    [Fact]
    public void SetWritesConfigDbSyncsSettingsAndNotifies()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = new Factory(connection);
        using (var db = factory.CreateDbContext()) db.Database.Migrate();
        var settings = new ScadaSettings { ShiftTimeZone = "UTC" };
        var calendar = new FactoryCalendarService(factory, Options.Create(settings));
        var notified = 0;
        calendar.Changed += () => notified++;

        var row = calendar.Set("Asia/Shanghai", "engineer");
        Assert.Equal("Asia/Shanghai", row.ShiftTimeZone);
        Assert.Equal("Asia/Shanghai", settings.ShiftTimeZone);
        Assert.Equal("Asia/Shanghai", calendar.ConfiguredId);
        Assert.Equal(1, notified);

        using (var db = factory.CreateDbContext())
        {
            var stored = db.SiteOperationsSettings.Single();
            Assert.Equal(1, stored.Id);
            Assert.Equal("Asia/Shanghai", stored.ShiftTimeZone);
            Assert.Equal("engineer", stored.UpdatedBy);
        }

        calendar.Set(null, "engineer");
        Assert.Null(calendar.ConfiguredId);
        Assert.True(string.IsNullOrWhiteSpace(settings.ShiftTimeZone));
        Assert.Equal(2, notified);
    }

    [Fact]
    public void SeedCopiesSettingsWhenTableEmptyAndRefreshRereads()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = new Factory(connection);
        using (var db = factory.CreateDbContext()) db.Database.Migrate();
        var settings = new ScadaSettings { ShiftTimeZone = "UTC" };
        var calendar = new FactoryCalendarService(factory, Options.Create(settings));
        calendar.SeedFromSettingsIfEmpty();
        Assert.Equal("UTC", calendar.ConfiguredId);
        using (var db = factory.CreateDbContext())
            Assert.Equal("UTC", db.SiteOperationsSettings.Single().ShiftTimeZone);

        using (var db = factory.CreateDbContext())
        {
            var row = db.SiteOperationsSettings.Single();
            row.ShiftTimeZone = "Asia/Shanghai";
            db.SaveChanges();
        }
        calendar.Refresh();
        Assert.Equal("Asia/Shanghai", calendar.ConfiguredId);
        Assert.Equal("Asia/Shanghai", settings.ShiftTimeZone);
    }

    [Fact]
    public void SetRejectsUnknownTimeZoneWithoutWriting()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var factory = new Factory(connection);
        using (var db = factory.CreateDbContext()) db.Database.Migrate();
        var settings = new ScadaSettings();
        var calendar = new FactoryCalendarService(factory, Options.Create(settings));
        Assert.Throws<ArgumentException>(() => calendar.Set("Mars/Olympus", "x"));
        using (var db = factory.CreateDbContext())
            Assert.Empty(db.SiteOperationsSettings);
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }
}
