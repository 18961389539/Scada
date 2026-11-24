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

public class AlarmEngineHaTests
{
    [Fact]
    public async Task RaiseReusesExistingOpenEvent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        await using (var db = runtime.CreateDbContext())
        {
            db.AlarmEvents.Add(new AlarmEvent { RuleId = 1, TagId = 1, DeviceId = 1, StartTs = 1000, Message = "active" });
            await db.SaveChangesAsync();
        }
        using var bus = new RealtimeBus();
        var cache = new RealtimeCache();
        using var engine = new AlarmEngine(bus, new Snapshot(delay: 0), cache, factory, runtime, new Notifier(), NullLogger<AlarmEngine>.Instance);
        await engine.StartAsync(CancellationToken.None);
        var value = new TagValue(1, 15, TagQuality.Good, ScadaTime.NowMs());
        cache.Set(value); bus.Publish(value);
        await Task.Delay(400);
        await using (var db = runtime.CreateDbContext()) Assert.Equal(1, await db.AlarmEvents.CountAsync());
        await engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StandbySkipsEvaluationAndDoesNotRaise()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var factory = new Factory(connection);
        await using var runtimeConnection = TestRuntime.Open(); var runtime = TestRuntime.Factory(runtimeConnection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var role = new ToggleRole { Active = false };
        using var bus = new RealtimeBus();
        var cache = new RealtimeCache();
        var notifier = new Notifier();
        using var engine = new AlarmEngine(bus, new Snapshot(delay: 0), cache, factory, runtime, notifier, NullLogger<AlarmEngine>.Instance, role: role);
        await engine.StartAsync(CancellationToken.None);
        var value = new TagValue(1, 15, TagQuality.Good, ScadaTime.NowMs());
        cache.Set(value); bus.Publish(value);
        await Task.Delay(400);
        await using (var db = runtime.CreateDbContext()) Assert.Empty(await db.AlarmEvents.ToListAsync());
        Assert.False(notifier.Raised.Task.IsCompleted);
        role.Active = true; role.Generation = 1;
        cache.Set(value); bus.Publish(value);
        var alarm = await notifier.Raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, alarm.RuleId);
        await engine.StopAsync(CancellationToken.None);
    }

    private sealed class ToggleRole : INodeRole
    {
        public bool Active { get; set; }
        public bool IsActive => Active;
        public long Generation { get; set; }
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }

    private sealed class Notifier : IAlarmNotifier
    {
        public TaskCompletionSource<AlarmEvent> Raised { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default)
        {
            if (action == "raise") Raised.TrySetResult(alarm);
            return Task.CompletedTask;
        }
    }

    private sealed class Snapshot(int delay) : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => [new() { Id = 1, PollIntervalMs = 100 }];
        public IReadOnlyList<Tag> Tags => [new() { Id = 1, DeviceId = 1, Name = "constant" }];
        public IReadOnlyList<AlarmRule> AlarmRules => [new() { Id = 1, TagId = 1, Type = AlarmRuleType.High, Threshold = 10, DelaySec = delay, Enabled = true }];
        public int Version => 1; public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag tag) { tag = Tags[0]; return id == 1; }
        public bool TryGetDevice(int id, out Device device) { device = Devices[0]; return id == 1; }
    }
}
