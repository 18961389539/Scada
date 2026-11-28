using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Licensing;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public sealed class MomentaryReleaseSafetyTests
{
    [Fact]
    public async Task ReleaseSkipsLicenseBlockAndDisabledUnwritableFlags()
    {
        await using var fx = await Fixture.CreateAsync(outputsAllowed: false);
        fx.Device.Enabled = false;
        fx.Tag.Writable = false;
        await using (var db = fx.Config.CreateDbContext())
        {
            db.Devices.Update(fx.Device);
            db.Tags.Update(fx.Tag);
            await db.SaveChangesAsync();
        }
        await fx.Snapshot.ReloadAsync();

        var recorded = MomentaryReleaseTarget.FromTag(fx.Tag);
        Assert.NotNull(recorded);
        var result = await fx.Commands.SubmitMomentaryReleaseAsync(
            fx.Tag.Id, 0, new ControlActor(1, "op"), "test", Guid.NewGuid(), default,
            recorded with { Address = 99 });

        Assert.True(result.Success, result.Message);
        var write = Assert.Single(fx.Collector.Writes);
        Assert.Equal(fx.Device.Id, write.DeviceId);
        Assert.Equal(99, write.Address);
        Assert.Equal(0, write.Value);
    }

    [Fact]
    public async Task ReleaseWritesRecordedTargetAfterTagAddressChanges()
    {
        await using var fx = await Fixture.CreateAsync();
        var target = MomentaryReleaseTarget.FromTag(fx.Tag);
        fx.Tag.Address = 50;
        await using (var db = fx.Config.CreateDbContext())
        {
            db.Tags.Update(fx.Tag);
            await db.SaveChangesAsync();
        }
        await fx.Snapshot.ReloadAsync();

        var result = await fx.Commands.SubmitMomentaryReleaseAsync(
            fx.Tag.Id, 0, new ControlActor(1, "op"), "test", Guid.NewGuid(), default, target);

        Assert.True(result.Success, result.Message);
        var write = Assert.Single(fx.Collector.Writes);
        Assert.Equal(target!.Address, write.Address);
        Assert.NotEqual(50, write.Address);
    }

    [Fact]
    public async Task AdoptOrphanLeasesReleasesRowsArrivingAfterFirstRecovery()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Momentary.AdoptOrphanLeasesAsync(default);
        Assert.Empty(fx.Collector.Writes);

        await using (var runtime = fx.Runtime.CreateDbContext())
        {
            var row = Lease(fx.Tag.Id, 0);
            MomentaryReleaseTarget.FromTag(fx.Tag)!.CopyTo(row);
            runtime.MomentaryLeases.Add(row);
            await runtime.SaveChangesAsync();
        }

        await fx.Momentary.AdoptOrphanLeasesAsync(default);
        Assert.Equal([0], fx.Collector.Writes.Select(x => x.Value).ToList());
        await using var after = fx.Runtime.CreateDbContext();
        Assert.Empty(after.MomentaryLeases);
    }

    [Fact]
    public async Task ReleaseBeforeChangeMessageNamesTheAction()
    {
        await using var fx = await Fixture.CreateAsync();
        fx.Collector.FailWrite = true;
        await using (var runtime = fx.Runtime.CreateDbContext())
        {
            runtime.MomentaryLeases.Add(Lease(fx.Tag.Id, 0));
            await runtime.SaveChangesAsync();
        }

        var result = await fx.Momentary.ReleaseBeforeChangeAsync([fx.Tag.Id], "修改", default);
        Assert.False(result.Success);
        Assert.Contains("后再修改", result.Error);
    }

    private static MomentaryLease Lease(int tagId, double releaseValue) => new()
    {
        Id = Guid.NewGuid(), TagId = tagId, ReleaseValue = releaseValue, UserName = "op", Source = "test",
        Kind = MomentaryLeaseKind.Press, State = MomentaryLeaseState.Active, ReleaseId = Guid.NewGuid(),
        CreatedTs = ScadaTime.NowMs(), StartDeadlineTs = ScadaTime.NowMs() + 15_000, ExpiresTs = ScadaTime.NowMs() + 30_000
    };

    private sealed class BlockedLicense : ILicenseEnforcement
    {
        public bool OutputsAllowed => false;
        public string BlockMessage => "outputs blocked";
        public int MaxDevices => 0;
        public int MaxTags => 0;
    }

    private sealed class FakeCollector : ICollectorControl
    {
        public bool FailWrite { get; set; }
        public List<(int DeviceId, int Address, double Value)> Writes { get; } = [];
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => [];
        public void RequestReload() { }
        public Task<IReadOnlyList<TagValue>> ReadAsync(int deviceId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TagValue>>([]);
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default)
        {
            Writes.Add((deviceId, tag.Address, value));
            return Task.FromResult(FailWrite
                ? new CommandExecutionResult(false, "write failed", Outcome: GatewayCommandOutcome.Failed)
                : new CommandExecutionResult(true, "ok", value));
        }
    }

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options);
    }

    private sealed class Fixture(
        string root,
        SqliteConnection connection,
        IDbContextFactory<ConfigDbContext> config,
        IDbContextFactory<RuntimeDbContext> runtime,
        FakeCollector collector,
        ControlCommandService commands,
        MomentaryControlService momentary,
        ConfigSnapshot snapshot,
        Device device,
        Tag tag) : IAsyncDisposable
    {
        public IDbContextFactory<ConfigDbContext> Config => config;
        public IDbContextFactory<RuntimeDbContext> Runtime => runtime;
        public FakeCollector Collector => collector;
        public ControlCommandService Commands => commands;
        public MomentaryControlService Momentary => momentary;
        public ConfigSnapshot Snapshot => snapshot;
        public Device Device => device;
        public Tag Tag => tag;

        public static async Task<Fixture> CreateAsync(bool outputsAllowed = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "scada-momentary-safety-" + Guid.NewGuid().ToString("N"));
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
            var tag = new Tag { Name = "jog", DeviceId = device.Id, DataType = TagDataType.UInt16, Writable = true, Address = 7, Scale = 1 };
            db.Tags.Add(tag);
            await db.SaveChangesAsync();
            var snapshot = new ConfigSnapshot(factory);
            await snapshot.ReloadAsync();
            var cache = new RealtimeCache();
            var collector = new FakeCollector();
            ILicenseEnforcement? license = outputsAllowed ? null : new BlockedLicense();
            var commands = new ControlCommandService(snapshot, collector, cache, factory, runtime, NullLogger<ControlCommandService>.Instance, role: new StandaloneNodeRole(), license: license);
            var momentary = new MomentaryControlService(commands, runtime, NullLogger<MomentaryControlService>.Instance);
            return new Fixture(root, connection, factory, runtime, collector, commands, momentary, snapshot, device, tag);
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
