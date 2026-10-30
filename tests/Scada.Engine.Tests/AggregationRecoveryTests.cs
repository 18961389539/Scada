using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Data.Infrastructure;
using Scada.Engine.Aggregation;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using HistoryValue = Scada.Core.Models.HistoryValue;

namespace Scada.Engine.Tests;

public sealed class AggregationRecoveryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static long Ts(DateTimeOffset time) => time.ToUnixTimeMilliseconds();
    private static HistoryValue Value(DateTimeOffset time, double value, int tag = 1, TagQuality quality = TagQuality.Good) =>
        new(tag, Ts(time), value, quality);

    [Fact]
    public async Task RestartBackfillsEveryRetainedWindowAcrossMonthsAndYearsAtAnArbitraryMinute()
    {
        using var site = new Site();
        var first = new DateTimeOffset(2025, 12, 31, 23, 55, 0, TimeSpan.Zero);
        var next = first.AddMinutes(5);
        await site.Store.WriteRawAsync([
            Value(first.AddSeconds(1), 2), Value(first.AddSeconds(2), 8),
            Value(first.AddSeconds(3), 1000, quality: TagQuality.Bad),
            Value(next.AddSeconds(1), 20), Value(next.AddMinutes(5), 30),
            Value(next.AddHours(8), 40), Value(next.AddHours(8), 50, tag: 2)
        ], default);

        using var restarted = site.Service(new HistoryStore(new HistoryDbFactory(Options.Create(site.Settings))));
        await restarted.RunOnceAsync(next.AddHours(12).AddMinutes(37), default);

        await using var oldDb = site.Factory.CreateAggregate(2025);
        var old = await oldDb.AggHour.SingleAsync();
        Assert.Equal(Ts(first.AddMinutes(-55)), old.Ts);
        Assert.Equal((5d, 2d, 8d, 2), (old.Avg, old.Min, old.Max, old.Count));
        await using var db = site.Factory.CreateAggregate(2026);
        var hours = await db.AggHour.OrderBy(x => x.Ts).ThenBy(x => x.TagId).ToListAsync();
        Assert.Equal(3, hours.Count);
        Assert.Equal((25d, 20d, 30d, 2), (hours[0].Avg, hours[0].Min, hours[0].Max, hours[0].Count));
        Assert.Equal(new[] { 25d, 40d, 50d }, hours.Select(x => x.Avg));
        Assert.Equal(0, await restarted.RunOnceAsync(next.AddHours(13), default));
    }

    [Fact]
    public async Task GatewayAndSpoolLateDataReopenCompletedWindowsWithoutDoubleCountingReplay()
    {
        using var site = new Site();
        await site.Store.WriteRawAsync([Value(Start, 10)], default);
        using (var service = site.Service()) await service.RunOnceAsync(Start.AddHours(2), default);
        await site.Store.WriteGatewayHistoryAsync([Value(Start.AddMinutes(4), 20)], default);
        var spool = new HistorySpool(site.Settings.HistoryRoot);
        await spool.AppendAsync([Value(Start.AddMinutes(6), 30)], default);
        await spool.DrainAsync(batch => site.Store.WriteRawAsync(batch, default), default);

        using var restarted = site.Service();
        await restarted.RunOnceAsync(Start.AddHours(3), default);
        await using var db = site.Factory.CreateAggregate(Start.Year);
        var hour = await db.AggHour.SingleAsync();
        Assert.Equal((20d, 10d, 30d, 3), (hour.Avg, hour.Min, hour.Max, hour.Count));
        Assert.Equal(2, await db.Agg5Min.CountAsync());

        await site.Store.WriteGatewayHistoryAsync([Value(Start.AddMinutes(4), 999)], default);
        await site.Store.WriteRawAsync([Value(Start.AddMinutes(6), 999)], default);
        Assert.Equal(0, await restarted.RunOnceAsync(Start.AddHours(4), default));
        Assert.Equal(3, (await db.AggHour.AsNoTracking().SingleAsync()).Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedWriteOrCrashAfterCommitKeepsWorkForRestart(bool hourly, bool afterCommit)
    {
        using var site = new Site();
        await site.Store.WriteRawAsync([Value(Start, 10), Value(Start.AddMinutes(5), 30)], default);
        var failing = new InterceptStore(site.Store) { FailHourly = hourly, FailAfterCommit = afterCommit };
        using (var service = site.Service(failing)) await service.RunOnceAsync(Start.AddHours(2), default);
        await using var pendingDb = hourly ? site.Factory.CreateAggregate(Start.Year) : site.Factory.CreateRaw(Start.Year, Start.Month);
        Assert.True(await pendingDb.PendingAggregations.AnyAsync());

        using var restarted = site.Service();
        await restarted.RunOnceAsync(Start.AddHours(3), default);
        await using var db = site.Factory.CreateAggregate(Start.Year);
        var hour = await db.AggHour.SingleAsync();
        Assert.Equal((20d, 2), (hour.Avg, hour.Count));
        Assert.Equal(2, await db.Agg5Min.CountAsync());
        Assert.Equal(0, await restarted.RunOnceAsync(Start.AddHours(4), default));
    }

    [Fact]
    public async Task SourceWriteDuringFiveMinuteCalculationCannotBeAcknowledgedAway()
    {
        using var site = new Site();
        await site.Store.WriteRawAsync([Value(Start, 10)], default);
        var store = new InterceptStore(site.Store)
        {
            BeforeFiveMinuteWrite = () => site.Store.WriteRawAsync([Value(Start.AddSeconds(1), 30)], default)
        };
        using var service = site.Service(store);
        await service.RunOnceAsync(Start.AddHours(2), default);
        await using var raw = site.Factory.CreateRaw(Start.Year, Start.Month);
        Assert.True(await raw.PendingAggregations.AnyAsync());
        await service.RunOnceAsync(Start.AddHours(2), default);
        Assert.False(await raw.PendingAggregations.AnyAsync());
        await using var db = site.Factory.CreateAggregate(Start.Year);
        Assert.Equal(20, (await db.Agg5Min.SingleAsync()).Avg);
        Assert.Equal(2, (await db.AggHour.SingleAsync()).Count);
    }

    [Fact]
    public async Task FiveMinuteWriteDuringHourCalculationKeepsHourPending()
    {
        using var site = new Site();
        await site.Store.WriteAggregate5MinAsync([new(1, Ts(Start), 10, 10, 10, 1)], default);
        var store = new InterceptStore(site.Store)
        {
            BeforeHourWrite = () => site.Store.WriteAggregate5MinAsync([new(1, Ts(Start.AddMinutes(5)), 30, 30, 30, 3)], default)
        };
        using var service = site.Service(store);
        await service.RunOnceAsync(Start.AddHours(2), default);
        await using var db = site.Factory.CreateAggregate(Start.Year);
        Assert.True(await db.PendingAggregations.AnyAsync());
        await service.RunOnceAsync(Start.AddHours(2), default);
        var hour = await db.AggHour.SingleAsync();
        Assert.Equal((25d, 10d, 30d, 4), (hour.Avg, hour.Min, hour.Max, hour.Count));
        Assert.False(await db.PendingAggregations.AnyAsync());
    }

    [Fact]
    public async Task OpenWindowsWaitForTheirUtcBoundary()
    {
        using var site = new Site();
        await site.Store.WriteRawAsync([Value(Start.AddMinutes(1), 10), Value(Start.AddMinutes(6), 30)], default);
        using var service = site.Service();
        await service.RunOnceAsync(Start.AddMinutes(7), default);
        await using var db = site.Factory.CreateAggregate(Start.Year);
        Assert.Single(await db.Agg5Min.ToListAsync());
        Assert.Empty(await db.AggHour.ToListAsync());
        await service.RunOnceAsync(Start.AddMinutes(10), default);
        Assert.Equal(2, await db.Agg5Min.CountAsync());
        Assert.Empty(await db.AggHour.ToListAsync());
        await service.RunOnceAsync(Start.AddHours(1), default);
        Assert.Equal(20, (await db.AggHour.SingleAsync()).Avg);
    }

    [Fact]
    public async Task StandbyAndDisabledNodesDeferWorkAndPromotionOrEnablingBackfillsIt()
    {
        using var site = new Site();
        await site.Store.WriteRawAsync([Value(Start, 10)], default);
        var role = new Role { IsActive = false };
        using var service = site.Service(role: role);
        Assert.Equal(0, await service.RunOnceAsync(Start.AddHours(2), default));
        Assert.False(File.Exists(site.Factory.GetAggregatePath(Start.Year)));
        role.IsActive = true; role.Generation++;
        site.Settings.AggregationEnabled = false;
        Assert.Equal(0, await service.RunOnceAsync(Start.AddHours(2), default));
        site.Settings.AggregationEnabled = true;
        Assert.True(await service.RunOnceAsync(Start.AddHours(2), default) > 0);
    }

    [Fact]
    public async Task LosingPrimaryRoleDuringQueryDoesNotPublishOrAcknowledgeCalculation()
    {
        using var site = new Site();
        await site.Store.WriteRawAsync([Value(Start, 10)], default);
        var role = new Role();
        var store = new InterceptStore(site.Store) { AfterRawQuery = () => { role.IsActive = false; role.Generation++; } };
        using var service = site.Service(store, role);
        Assert.Equal(0, await service.RunOnceAsync(Start.AddHours(2), default));
        Assert.False(File.Exists(site.Factory.GetAggregatePath(Start.Year)));
        await using var raw = site.Factory.CreateRaw(Start.Year, Start.Month);
        Assert.True(await raw.PendingAggregations.AnyAsync());
    }

    [Fact]
    public async Task UpgradeQueuesExistingRawAndRetainedFiveMinuteHistory()
    {
        using var site = new Site();
        var rawPath = site.Factory.GetRawPath(Start.Year, Start.Month);
        await using (var raw = await LegacyDatabase(rawPath))
            await raw.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO HistoryValue (TagId,Ts,Value,Quality) VALUES (1,{Ts(Start)},12,0)");
        var old = new DateTimeOffset(2025, 10, 20, 0, 0, 0, TimeSpan.Zero);
        await using (var agg = await LegacyDatabase(site.Factory.GetAggregatePath(old.Year)))
        {
            await agg.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Agg5Min (TagId,Ts,Avg,Min,Max,Count) VALUES (1,{Ts(old.AddMinutes(30))},10,10,10,1),(1,{Ts(old.AddHours(1))},20,20,20,1)");
            await agg.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO AggHour (TagId,Ts,Avg,Min,Max,Count) VALUES (1,{Ts(old)},99,99,99,10)");
        }
        using var service = site.Service();
        await service.RunOnceAsync(Start.AddHours(2), default);
        await using var db = site.Factory.CreateAggregate(Start.Year);
        Assert.Equal(12, (await db.AggHour.SingleAsync()).Avg);
        await using var oldDb = site.Factory.CreateAggregate(old.Year);
        var hours = await oldDb.AggHour.OrderBy(x => x.Ts).ToListAsync();
        Assert.Equal(new[] { 99d, 20d }, hours.Select(x => x.Avg));
        Assert.Equal(0, await service.RunOnceAsync(Start.AddHours(3), default));
    }

    [Fact]
    public async Task UpgradeAlsoFillsMissingFirstHourOfSparseFiveMinuteHistory()
    {
        using var site = new Site();
        await using (var db = await LegacyDatabase(site.Factory.GetAggregatePath(Start.Year)))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Agg5Min (TagId,Ts,Avg,Min,Max,Count) VALUES (1,{Ts(Start.AddMinutes(25))},17,10,20,3)");
        using var service = site.Service();
        await service.RunOnceAsync(Start.AddHours(2), default);
        await using var agg = site.Factory.CreateAggregate(Start.Year);
        var hour = await agg.AggHour.SingleAsync();
        Assert.Equal((Ts(Start), 17d, 10d, 20d, 3), (hour.Ts, hour.Avg, hour.Min, hour.Max, hour.Count));
    }

    [Fact]
    public async Task PendingWindowAndSourceSampleRollBackTogether()
    {
        using var site = new Site();
        await using var db = site.Factory.CreateRaw(Start.Year, Start.Month);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO HistoryValue (TagId,Ts,Value,Quality) VALUES (1,{Ts(Start)},12,0)");
            Assert.True(await db.PendingAggregations.AnyAsync());
            await transaction.RollbackAsync();
        }
        Assert.False(await db.PendingAggregations.AnyAsync());
        Assert.False(await db.HistoryValues.AnyAsync());
    }

    [Fact]
    public async Task CancellationKeepsUnacknowledgedWork()
    {
        using var site = new Site();
        using var cancellation = new CancellationTokenSource();
        await site.Store.WriteRawAsync([Value(Start, 10)], default);
        var store = new InterceptStore(site.Store) { AfterRawQuery = cancellation.Cancel };
        using var service = site.Service(store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunOnceAsync(Start.AddHours(2), cancellation.Token));
        await using var db = site.Factory.CreateRaw(Start.Year, Start.Month);
        Assert.True(await db.PendingAggregations.AnyAsync());
        using var restarted = site.Service();
        Assert.True(await restarted.RunOnceAsync(Start.AddHours(2), default) > 0);
    }

    [Fact]
    public async Task RetentionWaitsForPendingWorkThenRemovesSourcesButKeepsHours()
    {
        using var site = new Site();
        site.Settings.HistoryRetentionMonths = 1;
        site.Settings.Agg5MinRetentionDays = 7;
        var old = new DateTimeOffset(DateTime.UtcNow.Date.AddMonths(-3), TimeSpan.Zero);
        await site.Store.WriteRawAsync([Value(old, 10)], default);
        await site.Store.WriteAggregate5MinAsync([new(2, Ts(old.AddMinutes(5)), 20, 20, 20, 1)], default);
        using var maintenance = new MaintenanceService(Options.Create(site.Settings), site.Factory, NullLogger<MaintenanceService>.Instance);
        await maintenance.RunOnceAsync(default);
        Assert.True(File.Exists(site.Factory.GetRawPath(old.Year, old.Month)));
        await using (var db = site.Factory.CreateAggregate(old.Year)) Assert.True(await db.Agg5Min.AnyAsync());
        using var service = site.Service();
        await service.RunOnceAsync(DateTimeOffset.UtcNow, default);
        await maintenance.RunOnceAsync(default);
        Assert.False(File.Exists(site.Factory.GetRawPath(old.Year, old.Month)));
        await using var after = site.Factory.CreateAggregate(old.Year);
        Assert.False(await after.Agg5Min.AnyAsync());
        Assert.True(await after.AggHour.AnyAsync());
    }

    [Fact]
    public async Task BacklogLargerThanOneBatchIsNotSkipped()
    {
        using var site = new Site();
        await site.Store.WriteRawAsync(Enumerable.Range(0, 130)
            .Select(i => Value(Start.AddMinutes(5 * i), i)).ToArray(), default);
        using var service = site.Service();
        await service.RunOnceAsync(Start.AddDays(1), default);
        await using var raw = site.Factory.CreateRaw(Start.Year, Start.Month);
        Assert.Equal(2, await raw.PendingAggregations.CountAsync());
        await using var db = site.Factory.CreateAggregate(Start.Year);
        Assert.Equal(120, await db.AggHour.SumAsync(x => x.Count));
        await service.RunOnceAsync(Start.AddDays(1), default);
        Assert.Empty(await raw.PendingAggregations.ToListAsync());
        Assert.Equal(130, await db.Agg5Min.CountAsync());
        Assert.Equal(130, await db.AggHour.SumAsync(x => x.Count));
    }

    private static async Task<HistoryDbContext> LegacyDatabase(string path)
    {
        var db = new HistoryDbContext(new DbContextOptionsBuilder<HistoryDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);
        await db.GetService<IMigrator>().MigrateAsync("20261023161843_InitialCreate");
        return db;
    }

    private sealed class Role : INodeRole
    {
        public bool IsActive { get; set; } = true;
        public long Generation { get; set; }
    }

    private sealed class Site : IDisposable
    {
        public ScadaSettings Settings { get; }
        public HistoryDbFactory Factory { get; }
        public HistoryStore Store { get; }
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"scada-aggregation-{Guid.NewGuid():N}");

        public Site()
        {
            Settings = new ScadaSettings
            {
                HistoryRoot = Path.Combine(_root, "history"), ConfigDbPath = Path.Combine(_root, "missing-config.db"),
                BackupRoot = Path.Combine(_root, "backups"), AggregationEnabled = true
            };
            Factory = new HistoryDbFactory(Options.Create(Settings));
            Store = new HistoryStore(Factory);
        }

        public AggregationService Service(IHistoryStore? store = null, INodeRole? role = null) =>
            new(store ?? Store, Options.Create(Settings), NullLogger<AggregationService>.Instance, Factory, role);

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Settings.HistoryRoot, "*.db")) Factory.ReleaseFile(file);
            Directory.Delete(_root, true);
        }
    }

    private sealed class InterceptStore(IHistoryStore inner) : IHistoryStore
    {
        public bool? FailHourly { get; init; }
        public bool FailAfterCommit { get; init; }
        public Func<Task>? BeforeFiveMinuteWrite { get; set; }
        public Func<Task>? BeforeHourWrite { get; set; }
        public Action? AfterRawQuery { get; init; }

        public async Task WriteAggregate5MinAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken ct)
        {
            if (BeforeFiveMinuteWrite is { } action) { BeforeFiveMinuteWrite = null; await action(); }
            if (FailHourly == false && !FailAfterCommit) throw new IOException("Injected five-minute write failure");
            await inner.WriteAggregate5MinAsync(values, ct);
            if (FailHourly == false) throw new IOException("Injected crash after five-minute commit");
        }

        public async Task WriteAggregateHourAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken ct)
        {
            if (BeforeHourWrite is { } action) { BeforeHourWrite = null; await action(); }
            if (FailHourly == true && !FailAfterCommit) throw new IOException("Injected hour write failure");
            await inner.WriteAggregateHourAsync(values, ct);
            if (FailHourly == true) throw new IOException("Injected crash after hour commit");
        }

        public async Task<IReadOnlyList<AggregateValue>> QueryRawAggregatesAsync(long from, long to, CancellationToken ct)
        {
            var values = await inner.QueryRawAggregatesAsync(from, to, ct);
            AfterRawQuery?.Invoke();
            return values;
        }

        public Task WriteRawAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken ct) => inner.WriteRawAsync(values, ct);
        public Task<IReadOnlyList<HistorySeries>> QueryRawAsync(IReadOnlyCollection<int> ids, long from, long to, CancellationToken ct) => inner.QueryRawAsync(ids, from, to, ct);
        public Task<IReadOnlyList<HistorySeries>> QueryAggregate5MinAsync(IReadOnlyCollection<int> ids, long from, long to, CancellationToken ct) => inner.QueryAggregate5MinAsync(ids, from, to, ct);
        public Task<IReadOnlyList<HistorySeries>> QueryAggregateHourAsync(IReadOnlyCollection<int> ids, long from, long to, CancellationToken ct) => inner.QueryAggregateHourAsync(ids, from, to, ct);
        public Task<IReadOnlyList<AggregateValue>> Query5MinAggregatesAsync(long from, long to, CancellationToken ct) => inner.Query5MinAggregatesAsync(from, to, ct);
        public Task<IReadOnlyList<AggregatePoint>> QueryHourAggregatePointsAsync(IReadOnlyCollection<int> ids, long from, long to, CancellationToken ct) => inner.QueryHourAggregatePointsAsync(ids, from, to, ct);
        public Task<IReadOnlyList<AggregatePoint>> QueryAggregatePointsAsync(IReadOnlyCollection<int> ids, long from, long to, HistoryResolution resolution, CancellationToken ct) => inner.QueryAggregatePointsAsync(ids, from, to, resolution, ct);
    }
}
