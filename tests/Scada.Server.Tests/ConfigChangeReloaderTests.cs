using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Configuration;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Realtime;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class ConfigChangeReloaderTests
{
    [Fact]
    public async Task AlarmOnlyReloadDoesNotRequestCollectorReload()
    {
        var snapshot = new Snapshot { CollectionVersion = 3, AlarmVersion = 1, AddressSpaceVersion = 1 };
        var collector = new Collector();
        var reloader = new ConfigReloader(snapshot, collector, new RealtimeCache());

        await reloader.ReloadAsync();
        Assert.Equal(0, collector.ReloadRequests);

        snapshot.PendingAlarmVersion = 2;
        await reloader.ReloadAsync();
        Assert.Equal(0, collector.ReloadRequests);

        snapshot.PendingCollectionVersion = 4;
        await reloader.ReloadAsync();
        Assert.Equal(1, collector.ReloadRequests);
    }

    [Fact]
    public async Task RoleSwitchRequestsCollectorReloadEvenWithoutCollectionChange()
    {
        var snapshot = new Snapshot { CollectionVersion = 1 };
        var collector = new Collector();
        var role = new Role();
        var reloader = new ConfigReloader(snapshot, collector, new RealtimeCache(), role: role);
        await reloader.ReloadAsync();
        Assert.Equal(0, collector.ReloadRequests);

        role.Generation = 2;
        await reloader.ReloadAsync();
        Assert.Equal(1, collector.ReloadRequests);
    }

    [Fact]
    public async Task TrackerPublishesClassifiedChangesAndIgnoresFirstBaseline()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new ConfigFactory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync();

        var snapshot = new Snapshot { CollectionVersion = 1, AlarmVersion = 1, AddressSpaceVersion = 1 };
        var tracker = new ConfigChangeTracker(factory);
        await tracker.InitializeAsync(snapshot);
        var (first, firstCalendar) = await tracker.DiffAfterReloadAsync(snapshot);
        Assert.True(first.IsEmpty);
        Assert.False(firstCalendar);

        snapshot.AlarmVersion = 8;
        snapshot.AddressSpaceVersion = 1;
        var (alarms, _) = await tracker.DiffAfterReloadAsync(snapshot);
        Assert.True(alarms.Alarms);
        Assert.False(alarms.Collection);
        Assert.False(alarms.ExternalAddressSpace);

        await using (var db = factory.CreateDbContext())
        {
            db.CalcJobs.Add(new CalcJob { Name = "job", Script = "return 1;", Enabled = true, Revision = 1 });
            await db.SaveChangesAsync();
        }
        var (jobs, _) = await tracker.DiffAfterReloadAsync(snapshot);
        Assert.True(jobs.Jobs);
        Assert.False(jobs.Alarms);
    }

    private sealed class Snapshot : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => [];
        public IReadOnlyList<Tag> Tags => [];
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version { get; set; } = 1;
        public int CollectionVersion { get; set; } = 1;
        public int AlarmVersion { get; set; } = 1;
        public int AddressSpaceVersion { get; set; } = 1;
        public int? PendingCollectionVersion { get; set; }
        public int? PendingAlarmVersion { get; set; }
        public int? PendingAddressSpaceVersion { get; set; }
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default)
        {
            if (PendingCollectionVersion is int collection) CollectionVersion = collection;
            if (PendingAlarmVersion is int alarm) AlarmVersion = alarm;
            if (PendingAddressSpaceVersion is int address) AddressSpaceVersion = address;
            return Task.CompletedTask;
        }
        public bool TryGetTag(int tagId, out Tag tag) { tag = null!; return false; }
        public bool TryGetDevice(int deviceId, out Device device) { device = null!; return false; }
    }

    private sealed class Collector : ICollectorControl
    {
        public int ReloadRequests { get; private set; }
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => [];
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default)
            => Task.FromResult(new CommandExecutionResult(false, "unused"));
        public void RequestReload() => ReloadRequests++;
    }

    private sealed class Role : INodeRole
    {
        public bool IsActive => true;
        public long Generation { get; set; } = 1;
    }

    private sealed class ConfigFactory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        private readonly DbContextOptions<ConfigDbContext> _options =
            new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options;
        public ConfigDbContext CreateDbContext() => new(_options);
        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
