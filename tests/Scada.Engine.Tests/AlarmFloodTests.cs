using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public class AlarmFloodGuardTests
{
    private const long T0 = 1_700_000_000_000;

    private static ScadaSettings Settings(int threshold = 10, bool autoSuppress = true, bool firstOut = true) => new()
    {
        AlarmFloodThreshold = threshold,
        AlarmFloodAutoSuppress = autoSuppress,
        AlarmFloodFirstOut = firstOut,
        AlarmFloodSuppressMaxLevel = AlarmLevel.Warning
    };

    [Fact]
    public void OnRaise_EntersFloodOnlyAboveStartThreshold()
    {
        var guard = new AlarmFloodGuard(Settings(threshold: 2));
        Assert.Equal(2, guard.StartThreshold);
        Assert.Equal(1, guard.ExitThreshold);

        Assert.False(guard.OnRaise(1, T0).InFlood);
        Assert.False(guard.OnRaise(1, T0 + 1_000).InFlood);

        var entered = guard.OnRaise(1, T0 + 2_000);
        Assert.True(entered.Entered);
        Assert.True(entered.InFlood);
        Assert.True(entered.Suppress);
        Assert.Equal(3, entered.Count);
        Assert.True(guard.IsInFlood(1, T0 + 2_000));
    }

    [Fact]
    public void OnRaise_KeepsFloodUntilCountDropsBelowExitThreshold()
    {
        var guard = new AlarmFloodGuard(Settings(threshold: 10));
        Assert.Equal(5, guard.ExitThreshold);
        for (var i = 0; i < 11; i++) guard.OnRaise(7, T0 + i * 1_000);
        Assert.True(guard.IsInFlood(7, T0 + 10_000));

        // 窗口内仍有 11 条：滞回区间内保持洪水，不因为一次 Tick 就退出。
        Assert.Empty(guard.Tick(T0 + 100_000));
        Assert.True(guard.IsInFlood(7, T0 + 100_000));

        // 10 分钟窗口滑过后计数归零，Tick 报出退出。
        var exited = guard.Tick(T0 + 700_000);
        Assert.Equal(new int?[] { 7 }, exited);
        Assert.False(guard.IsInFlood(7, T0 + 700_000));
    }

    [Fact]
    public void OnRaise_DropsRaisesOutsideRollingWindow()
    {
        var guard = new AlarmFloodGuard(Settings(threshold: 2));
        guard.OnRaise(1, T0);
        guard.OnRaise(1, T0 + 1_000);
        // 前两条已滑出窗口，第三条不构成洪水。
        var decision = guard.OnRaise(1, T0 + AlarmFloodGuard.WindowMs + 2_000);
        Assert.Equal(1, decision.Count);
        Assert.False(decision.InFlood);
    }

    [Fact]
    public void Flood_IsTrackedPerArea()
    {
        var guard = new AlarmFloodGuard(Settings(threshold: 1));
        guard.OnRaise(1, T0);
        guard.OnRaise(1, T0 + 10);
        guard.OnRaise(null, T0 + 20);

        Assert.True(guard.IsInFlood(1, T0 + 30));
        Assert.False(guard.IsInFlood(2, T0 + 30));
        Assert.False(guard.IsInFlood(null, T0 + 30));

        var snapshot = guard.Snapshot(T0 + 30);
        Assert.Equal(2, snapshot.Count);
        var area1 = snapshot.Single(x => x.AreaId == 1);
        Assert.True(area1.InFlood);
        Assert.Equal(2, area1.Count);
        Assert.Equal(2, area1.Peak);
        Assert.Equal(T0 + 10, area1.FloodStartTs);
        Assert.Equal(T0 + 30 - AlarmFloodGuard.WindowMs, area1.WindowStartTs);
        Assert.Equal(T0 + 30, area1.WindowEndTs);
        Assert.Equal(1, snapshot.Single(x => x.AreaId is null).Threshold);
        Assert.False(snapshot.Single(x => x.AreaId is null).InFlood);
    }

    [Fact]
    public void Suppress_FollowsPolicy()
    {
        var off = new AlarmFloodGuard(Settings(threshold: 1, autoSuppress: false));
        off.OnRaise(3, T0);
        Assert.False(off.OnRaise(3, T0 + 1).Suppress);
        Assert.True(off.OnRaise(3, T0 + 2).InFlood);

        var on = new AlarmFloodGuard(Settings(threshold: 1));
        Assert.True(on.FirstOutEnabled);
        on.OnRaise(3, T0);
        Assert.True(on.OnRaise(3, T0 + 1).Suppress);
    }

    [Fact]
    public void SuppressedIds_AreTrackedAndReleasedOnce()
    {
        var guard = new AlarmFloodGuard(Settings(threshold: 1));
        guard.OnRaise(4, T0);
        guard.TrackSuppressed(4, 11);
        guard.TrackSuppressed(4, 12);
        guard.TrackSuppressed(5, 13);
        Assert.True(guard.IsFloodSuppressed(4, 11));
        Assert.False(guard.IsFloodSuppressed(5, 11));
        Assert.Equal(2, guard.Snapshot(T0).Single(x => x.AreaId == 4).SuppressedCount);

        guard.RemoveSuppressed(4, 11);
        Assert.False(guard.IsFloodSuppressed(4, 11));

        Assert.Equal(new[] { 12 }, guard.ReleaseSuppressed(4));
        Assert.Empty(guard.ReleaseSuppressed(4));
        Assert.Equal(new[] { 13 }, guard.ReleaseSuppressed(5));
        Assert.Empty(guard.ReleaseSuppressed(9));
    }

    [Fact]
    public void FirstOut_IsResetWhenFloodEnds()
    {
        var guard = new AlarmFloodGuard(Settings(threshold: 1));
        guard.OnRaise(1, T0);
        guard.OnRaise(1, T0 + 1);
        guard.MarkFirstOut(1, 42);
        Assert.Equal(42, guard.Snapshot(T0 + 2).Single(x => x.AreaId == 1).FirstOutEventId);

        guard.Tick(T0 + AlarmFloodGuard.WindowMs + 10);
        Assert.False(guard.IsInFlood(1, T0 + AlarmFloodGuard.WindowMs + 10));
        // 洪水片段结束后不再上报旧的首出，避免 UI 把上一班次的根因当成当前状态。
        Assert.Empty(guard.Snapshot(T0 + AlarmFloodGuard.WindowMs + 10));
    }
}

public class AlarmEngineFloodTests
{
    [Fact]
    public async Task Flood_MarksFirstOut_SuppressesLowLevelAndKeepsCriticalAnnunciated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();

        using var bus = new RealtimeBus();
        var cache = new RealtimeCache();
        var notifier = new Recorder();
        var settings = new ScadaSettings { AlarmFloodThreshold = 1, AlarmFloodAutoSuppress = true, AlarmFloodFirstOut = true, AlarmFloodSuppressMaxLevel = AlarmLevel.Warning };
        using var engine = new AlarmEngine(bus, new Snapshot(), cache, factory, notifier, NullLogger<AlarmEngine>.Instance,
            options: Options.Create(settings), floodGuard: new AlarmFloodGuard(settings));
        await engine.StartAsync(CancellationToken.None);

        var ts = ScadaTime.NowMs();
        Publish(bus, cache, 1, 15, ts);        // Info：洪水前第一条，应成为首出
        Publish(bus, cache, 2, 15, ts + 1);    // Warning：触发洪水，应被自动抑制
        Publish(bus, cache, 3, 15, ts + 2);    // Critical：洪水中也必须发声

        await notifier.WaitForAsync(x => x.Count(y => y.Action == "raise") == 3 && x.Any(y => y.Action == "suppress"));
        var suppressed = notifier.Snapshot().Single(x => x.Action == "suppress").Alarm;
        Assert.Equal(2, suppressed.TagId);
        Assert.StartsWith("报警洪水", suppressed.SuppressReason ?? string.Empty);

        // 被洪水抑制的报警恢复后应清掉抑制标记，进入 RTN 未确认，而不是随洪水静默消失。
        Publish(bus, cache, 2, 0, ts + 3_000);
        await notifier.WaitForAsync(x => x.Any(y => y.Action == "recover" && y.Alarm.Id == suppressed.Id));
        var recovered = notifier.Snapshot().Single(x => x.Action == "recover").Alarm;
        Assert.NotNull(recovered.EndTs);
        Assert.False(recovered.Suppressed);
        Assert.Null(recovered.SuppressReason);

        await engine.StopAsync(CancellationToken.None);
        await using var check = factory.CreateDbContext();
        var alarms = await check.AlarmEvents.OrderBy(x => x.Id).ToListAsync();
        var info = alarms.Single(x => x.TagId == 1);
        var warning = alarms.Single(x => x.TagId == 2);
        var critical = alarms.Single(x => x.TagId == 3);
        Assert.True(info.FirstOut);
        Assert.False(info.Suppressed);
        Assert.False(warning.FirstOut);
        Assert.False(warning.Suppressed);
        Assert.NotNull(warning.EndTs);
        Assert.False(critical.Suppressed);
        Assert.False(critical.FirstOut);
        Assert.Contains(await check.AlarmActivities.ToListAsync(), x => x.Action == "firstout" && x.AlarmEventId == info.Id);
    }

    [Fact]
    public async Task FloodInOneArea_DoesNotSuppressAlarmsOfAnotherArea()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();

        using var bus = new RealtimeBus();
        var cache = new RealtimeCache();
        var notifier = new Recorder();
        var settings = new ScadaSettings { AlarmFloodThreshold = 1 };
        using var engine = new AlarmEngine(bus, new Snapshot(), cache, factory, notifier, NullLogger<AlarmEngine>.Instance,
            options: Options.Create(settings), floodGuard: new AlarmFloodGuard(settings));
        await engine.StartAsync(CancellationToken.None);

        var ts = ScadaTime.NowMs();
        Publish(bus, cache, 1, 15, ts);
        Publish(bus, cache, 2, 15, ts + 1);
        Publish(bus, cache, 4, 15, ts + 2);    // 区域 2 的第一条报警

        await notifier.WaitForAsync(x => x.Count(y => y.Action == "raise") == 3 && x.Any(y => y.Action == "suppress"));
        await engine.StopAsync(CancellationToken.None);

        await using var check = factory.CreateDbContext();
        var alarms = await check.AlarmEvents.OrderBy(x => x.Id).ToListAsync();
        Assert.True(alarms.Single(x => x.TagId == 1).FirstOut);
        Assert.True(alarms.Single(x => x.TagId == 2).Suppressed);
        var other = alarms.Single(x => x.TagId == 4);
        Assert.False(other.Suppressed);
        Assert.False(other.FirstOut);
    }

    private static void Publish(RealtimeBus bus, RealtimeCache cache, int tagId, double value, long ts)
    {
        var tagValue = new TagValue(tagId, value, TagQuality.Good, ts);
        cache.Set(tagValue);
        bus.Publish(tagValue);
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }

    // 引擎与测试共享同一条 SQLite 连接，所以断言统一等引擎停下后再查库，运行期只观察推送。
    private sealed class Recorder : IAlarmNotifier
    {
        private readonly List<(AlarmEvent Alarm, string Action)> _items = [];

        public IReadOnlyList<(AlarmEvent Alarm, string Action)> Snapshot()
        {
            lock (_items) return _items.ToList();
        }

        public async Task WaitForAsync(Func<IReadOnlyList<(AlarmEvent Alarm, string Action)>, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (predicate(Snapshot())) return;
                await Task.Delay(20);
            }
            Assert.Fail("等待报警推送超时");
        }

        public Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default)
        {
            lock (_items) _items.Add((alarm, action));
            return Task.CompletedTask;
        }
    }

    // 区域 1：Info/Warning/Critical 三条同级阈值规则；区域 2：一条 Warning 规则，用于验证隔离。
    private sealed class Snapshot : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices =>
        [
            new() { Id = 1, AreaId = 1, PollIntervalMs = 100 },
            new() { Id = 2, AreaId = 2, PollIntervalMs = 100 }
        ];
        public IReadOnlyList<Tag> Tags =>
        [
            new() { Id = 1, DeviceId = 1, Name = "info" },
            new() { Id = 2, DeviceId = 1, Name = "warning" },
            new() { Id = 3, DeviceId = 1, Name = "critical" },
            new() { Id = 4, DeviceId = 2, Name = "other-area" }
        ];
        public IReadOnlyList<AlarmRule> AlarmRules =>
        [
            new() { Id = 1, TagId = 1, Type = AlarmRuleType.High, Threshold = 10, Level = AlarmLevel.Info, Enabled = true },
            new() { Id = 2, TagId = 2, Type = AlarmRuleType.High, Threshold = 10, Level = AlarmLevel.Warning, Enabled = true },
            new() { Id = 3, TagId = 3, Type = AlarmRuleType.High, Threshold = 10, Level = AlarmLevel.Critical, Enabled = true },
            new() { Id = 4, TagId = 4, Type = AlarmRuleType.High, Threshold = 10, Level = AlarmLevel.Warning, Enabled = true }
        ];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag tag) => (tag = Tags.FirstOrDefault(x => x.Id == id)!) is not null;
        public bool TryGetDevice(int id, out Device device) => (device = Devices.FirstOrDefault(x => x.Id == id)!) is not null;
    }
}
