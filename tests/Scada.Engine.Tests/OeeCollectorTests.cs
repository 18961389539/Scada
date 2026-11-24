using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Oee;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

// 采集器在状态/计数测点质量无效时冻结上一拍，不把该段记入可用时间，恢复后按上次好值算增量。
public class OeeCollectorTests
{
    private const long Minute = 60_000;

    [Fact]
    public async Task BadStateQualityDoesNotAccrueRunTimeAndResumesCountsFromLastGood()
    {
        await using var fx = await Fixture.CreateAsync();
        var t0 = new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();

        fx.Set(fx.StateTag, 1, TagQuality.Good, t0);
        fx.Set(fx.GoodTag, 100, TagQuality.Good, t0);
        await fx.Collector.SampleOnceAsync(t0, default);

        fx.Set(fx.StateTag, 1, TagQuality.Bad, t0 + Minute);
        fx.Set(fx.GoodTag, 999, TagQuality.Bad, t0 + Minute);
        await fx.Collector.SampleOnceAsync(t0 + Minute, default);

        fx.Set(fx.StateTag, 1, TagQuality.CommFailure, t0 + 2 * Minute);
        fx.Set(fx.GoodTag, 999, TagQuality.Uncertain, t0 + 2 * Minute);
        await fx.Collector.SampleOnceAsync(t0 + 2 * Minute, default);

        fx.Set(fx.StateTag, 1, TagQuality.Good, t0 + 3 * Minute);
        fx.Set(fx.GoodTag, 110, TagQuality.Good, t0 + 3 * Minute);
        await fx.Collector.SampleOnceAsync(t0 + 3 * Minute, default);

        await using (var runtime = fx.Runtime.CreateDbContext())
        {
            var sample = Assert.Single(runtime.OeeLineSamples);
            Assert.Equal(LineState.Running, sample.State);
            Assert.Equal(110, sample.GoodCount);
            Assert.Equal(t0 + 3 * Minute, sample.Ts);
            // 坏质量期间（及恢复当拍之前的间隔）不记入可用时间；恢复当拍把 100→110 记为增量。
            Assert.Equal(0, runtime.OeeRollups.Sum(x => x.RunMs));
            Assert.Equal(0, runtime.OeeRollups.Sum(x => x.DownMs));
            Assert.Equal(0, runtime.OeeRollups.Sum(x => x.PlannedMs));
            Assert.Equal(10, runtime.OeeRollups.Sum(x => x.GoodCount));
            Assert.Empty(runtime.DowntimeEvents);
        }

        // 恢复后再采一拍，时间才开始按运行累加。
        fx.Set(fx.StateTag, 1, TagQuality.Good, t0 + 4 * Minute);
        fx.Set(fx.GoodTag, 110, TagQuality.Good, t0 + 4 * Minute);
        await fx.Collector.SampleOnceAsync(t0 + 4 * Minute, default);

        await using (var runtime = fx.Runtime.CreateDbContext())
        {
            Assert.Equal(Minute, runtime.OeeRollups.Sum(x => x.RunMs));
            Assert.Equal(10, runtime.OeeRollups.Sum(x => x.GoodCount));
        }
    }

    [Fact]
    public async Task BadStateQualityDoesNotCloseOpenDowntime()
    {
        await using var fx = await Fixture.CreateAsync();
        var t0 = new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();

        fx.Set(fx.StateTag, 0, TagQuality.Good, t0);
        fx.Set(fx.GoodTag, 50, TagQuality.Good, t0);
        await fx.Collector.SampleOnceAsync(t0, default);

        fx.Set(fx.StateTag, 1, TagQuality.Bad, t0 + Minute);
        fx.Set(fx.GoodTag, 50, TagQuality.Bad, t0 + Minute);
        await fx.Collector.SampleOnceAsync(t0 + Minute, default);

        await using var runtime = fx.Runtime.CreateDbContext();
        var ev = Assert.Single(runtime.DowntimeEvents);
        Assert.Equal(LineState.Stopped, ev.State);
        Assert.Null(ev.EndTs);
        Assert.Equal(LineState.Stopped, Assert.Single(runtime.OeeLineSamples).State);
        Assert.Equal(50, Assert.Single(runtime.OeeLineSamples).GoodCount);
        Assert.Equal(0, runtime.OeeRollups.Sum(x => x.RunMs));
        Assert.Equal(0, runtime.OeeRollups.Sum(x => x.DownMs));
    }

    [Fact]
    public async Task LineWithoutStateTagStillDefaultsToRunningWhenCountsAreBad()
    {
        await using var fx = await Fixture.CreateAsync(stateTag: false);
        var t0 = new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();

        fx.Set(fx.GoodTag, 20, TagQuality.Good, t0);
        await fx.Collector.SampleOnceAsync(t0, default);

        fx.Set(fx.GoodTag, 99, TagQuality.Bad, t0 + Minute);
        await fx.Collector.SampleOnceAsync(t0 + Minute, default);

        fx.Set(fx.GoodTag, 35, TagQuality.Good, t0 + 2 * Minute);
        await fx.Collector.SampleOnceAsync(t0 + 2 * Minute, default);

        await using var runtime = fx.Runtime.CreateDbContext();
        var sample = Assert.Single(runtime.OeeLineSamples);
        Assert.Equal(LineState.Running, sample.State);
        Assert.Equal(35, sample.GoodCount);
        Assert.Equal(2 * Minute, runtime.OeeRollups.Sum(x => x.RunMs));
        Assert.Equal(15, runtime.OeeRollups.Sum(x => x.GoodCount));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _configConnection;
        private readonly SqliteConnection _runtimeConnection;

        public int StateTag { get; } = 11;
        public int GoodTag { get; } = 12;
        public RealtimeCache Cache { get; }
        public IDbContextFactory<RuntimeDbContext> Runtime { get; }
        public OeeCollectorService Collector { get; }

        private Fixture(SqliteConnection configConnection, SqliteConnection runtimeConnection,
            RealtimeCache cache, IDbContextFactory<RuntimeDbContext> runtime, OeeCollectorService collector)
        {
            _configConnection = configConnection;
            _runtimeConnection = runtimeConnection;
            Cache = cache;
            Runtime = runtime;
            Collector = collector;
        }

        public static async Task<Fixture> CreateAsync(bool stateTag = true)
        {
            var configConnection = new SqliteConnection("Data Source=:memory:");
            await configConnection.OpenAsync();
            var runtimeConnection = TestRuntime.Open();
            var runtime = TestRuntime.Factory(runtimeConnection);
            var configOptions = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(configConnection).Options;
            await using (var db = new ConfigDbContext(configOptions))
            {
                await db.Database.MigrateAsync();
                db.Devices.Add(new Device { Id = 7, Name = "plc", AreaId = Area.DefaultId });
                db.Tags.AddRange(
                    new Tag { Id = 11, DeviceId = 7, Name = "state" },
                    new Tag { Id = 12, DeviceId = 7, Name = "good" });
                await db.SaveChangesAsync();
                db.ProductionLines.Add(new ProductionLine
                {
                    Name = "L1",
                    AreaId = Area.DefaultId,
                    Enabled = true,
                    StateTagId = stateTag ? 11 : null,
                    StateMapJson = """{"0":"Stopped","1":"Running"}""",
                    GoodCountTagId = 12
                });
                await db.SaveChangesAsync();
            }

            var cache = new RealtimeCache();
            var collector = new OeeCollectorService(
                new ServiceCollection().BuildServiceProvider(),
                new MemoryConfigFactory(configConnection),
                runtime,
                cache,
                Options.Create(new ScadaSettings()),
                NullLogger<OeeCollectorService>.Instance);
            return new Fixture(configConnection, runtimeConnection, cache, runtime, collector);
        }

        public void Set(int tagId, double value, TagQuality quality, long ts) =>
            Cache.Set(new TagValue(tagId, value, quality, ts));

        public async ValueTask DisposeAsync()
        {
            await _configConnection.DisposeAsync();
            await _runtimeConnection.DisposeAsync();
        }
    }

    private sealed class MemoryConfigFactory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }
}
