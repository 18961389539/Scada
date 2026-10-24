using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public class AlarmEngineIntegrationTests
{
    [Fact]
    public async Task ConstantOverLimitValue_RaisesDelayedAlarmWithoutAnotherChangeEvent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        using var bus = new RealtimeBus();
        var cache = new RealtimeCache(); var notifier = new Notifier();
        using var engine = new AlarmEngine(bus, new Snapshot(), cache, factory, notifier, NullLogger<AlarmEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        var value = new TagValue(1, 15, TagQuality.Good, ScadaTime.NowMs());
        cache.Set(value); bus.Publish(value); // Only one event, while the input remains constantly above the threshold.
        var alarm = await notifier.Raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, alarm.RuleId); Assert.True(alarm.StartTs >= value.Ts + 1000);
        await engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Restart_DoesNotMarkActiveTagAlarmRecovered()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.AlarmEvents.Add(new AlarmEvent { RuleId = 1, TagId = 1, DeviceId = 1, StartTs = 1000, Message = "active" });
            await db.SaveChangesAsync();
        }
        using var bus = new RealtimeBus(); using var engine = new AlarmEngine(bus, new Snapshot(), new RealtimeCache(), factory, new Notifier(), NullLogger<AlarmEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        await Task.Delay(400);
        await using (var db = factory.CreateDbContext()) Assert.Null((await db.AlarmEvents.SingleAsync()).EndTs);
        await engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Restart_DoesNotCloseADeviceOfflineAlarmOwnedByTheGatewayMonitor()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync(); db.AlarmEvents.Add(new() { DeviceId = 1, GatewayId = 7, StartTs = 1000, Message = "gateway offline", AckUser = "operator", AckTs = 1100 });
            await db.SaveChangesAsync();
        }
        using var bus = new RealtimeBus(); using var engine = new AlarmEngine(bus, new Snapshot(), new RealtimeCache(), factory, new Notifier(), NullLogger<AlarmEngine>.Instance);
        await engine.StartAsync(default); await Task.Delay(400);
        await using (var db = factory.CreateDbContext()) { var alarm = await db.AlarmEvents.SingleAsync(); Assert.Null(alarm.EndTs); Assert.Equal("operator", alarm.AckUser); }
        await engine.StopAsync(default);
    }
    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }
    private sealed class Notifier : IAlarmNotifier
    {
        public TaskCompletionSource<AlarmEvent> Raised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default) { if (action == "raise") Raised.TrySetResult(alarm); return Task.CompletedTask; }
    }
    private sealed class Snapshot : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => [new() { Id = 1, PollIntervalMs = 100 }];
        public IReadOnlyList<Tag> Tags => [new() { Id = 1, DeviceId = 1, Name = "constant" }];
        public IReadOnlyList<AlarmRule> AlarmRules => [new() { Id = 1, TagId = 1, Type = AlarmRuleType.High, Threshold = 10, DelaySec = 1, Enabled = true }];
        public int Version => 1; public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag tag) { tag = Tags[0]; return id == 1; }
        public bool TryGetDevice(int id, out Device device) { device = Devices[0]; return id == 1; }
    }
}
