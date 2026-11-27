using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public sealed class MomentaryReleaseBeforeDeleteTests
{
    [Fact]
    public async Task ReleasesDatabaseLeaseThenAllowsDelete()
    {
        await using var fx = await Fixture.CreateAsync();
        await using (var runtime = fx.Runtime.CreateDbContext())
        {
            runtime.MomentaryLeases.Add(Lease(fx.TagId, 0));
            await runtime.SaveChangesAsync();
        }

        var result = await fx.Momentary.ReleaseBeforeDeleteAsync([fx.TagId], default);
        Assert.True(result.Success, result.Error);
        Assert.Equal([0], fx.Collector.Writes);
        await using var after = fx.Runtime.CreateDbContext();
        Assert.Empty(after.MomentaryLeases);
    }

    [Fact]
    public async Task RefusesDeleteWhenReleaseWriteFails()
    {
        await using var fx = await Fixture.CreateAsync();
        fx.Collector.FailWrite = true;
        await using (var runtime = fx.Runtime.CreateDbContext())
        {
            runtime.MomentaryLeases.Add(Lease(fx.TagId, 0));
            await runtime.SaveChangesAsync();
        }

        var result = await fx.Momentary.ReleaseBeforeDeleteAsync([fx.TagId], default);
        Assert.False(result.Success);
        Assert.Contains(fx.TagId, result.FailedTagIds);
        Assert.Contains("尚未释放", result.Error);
        await using var after = fx.Runtime.CreateDbContext();
        Assert.Single(after.MomentaryLeases);
    }

    [Fact]
    public async Task SucceedsWhenThereIsNoLease()
    {
        await using var fx = await Fixture.CreateAsync();
        var result = await fx.Momentary.ReleaseBeforeDeleteAsync([fx.TagId], default);
        Assert.True(result.Success);
        Assert.Empty(fx.Collector.Writes);
    }

    private static MomentaryLease Lease(int tagId, double releaseValue) => new()
    {
        Id = Guid.NewGuid(), TagId = tagId, ReleaseValue = releaseValue, UserName = "op", Source = "test",
        Kind = MomentaryLeaseKind.Press, State = MomentaryLeaseState.Active, ReleaseId = Guid.NewGuid(),
        CreatedTs = ScadaTime.NowMs(), StartDeadlineTs = ScadaTime.NowMs() + 15_000, ExpiresTs = ScadaTime.NowMs() + 30_000
    };

    private sealed class FakeCollector : ICollectorControl
    {
        public bool FailWrite { get; set; }
        public List<double> Writes { get; } = [];
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => [];
        public void RequestReload() { }
        public Task<IReadOnlyList<TagValue>> ReadAsync(int deviceId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TagValue>>([]);
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default)
        {
            Writes.Add(value);
            return Task.FromResult(FailWrite
                ? new CommandExecutionResult(false, "write failed", Outcome: GatewayCommandOutcome.Failed)
                : new CommandExecutionResult(true, "ok", value));
        }
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }

    private sealed class Fixture(string root, SqliteConnection connection, IDbContextFactory<RuntimeDbContext> runtime, FakeCollector collector, MomentaryControlService momentary, int tagId) : IAsyncDisposable
    {
        public IDbContextFactory<RuntimeDbContext> Runtime => runtime;
        public FakeCollector Collector => collector;
        public MomentaryControlService Momentary => momentary;
        public int TagId => tagId;

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "scada-momentary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var connection = new SqliteConnection($"Data Source={Path.Combine(root, "scada.db")};Pooling=False");
            await connection.OpenAsync();
            var factory = new Factory(connection);
            var runtime = TestRuntime.Factory(Path.Combine(root, "runtime.db"));
            await using var db = factory.CreateDbContext();
            await db.Database.MigrateAsync();
            var device = new Device { Name = "PLC", Enabled = true, AreaId = Area.DefaultId };
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            var tag = new Tag { Name = "jog", DeviceId = device.Id, DataType = TagDataType.UInt16, Writable = true };
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            var snapshot = new ConfigSnapshot(factory);
            await snapshot.ReloadAsync();
            var cache = new RealtimeCache();
            var collector = new FakeCollector();
            var commands = new ControlCommandService(snapshot, collector, cache, factory, runtime, NullLogger<ControlCommandService>.Instance, role: new StandaloneNodeRole());
            var momentary = new MomentaryControlService(commands, runtime, NullLogger<MomentaryControlService>.Instance);
            return new Fixture(root, connection, runtime, collector, momentary, tag.Id);
        }

        public async ValueTask DisposeAsync()
        {
            Momentary.Dispose();
            await connection.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* 句柄释放失败不影响断言 */ }
        }
    }
}
