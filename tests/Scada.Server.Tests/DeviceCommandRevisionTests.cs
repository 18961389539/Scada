using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Simulation;
using Scada.Data;
using Scada.Engine.Realtime;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class DeviceCommandRevisionTests
{
    [Fact]
    public async Task UpdateRequiresMatchingTokenButAllowsTokenlessWrites()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options;
        await using var db = new ConfigDbContext(options);
        await db.Database.MigrateAsync();

        var service = new DeviceCommandService(
            new ConfigReloader(new EmptySnapshot(), new Collector(), new RealtimeCache()),
            Options.Create(new ScadaSettings()),
            new Drivers(),
            new Simulations());

        var device = new Device { Name = "dev", AreaId = Area.DefaultId, Revision = 3, Enabled = false };
        var conflict = await service.UpdateAsync(db, device, default, expectedRevision: 1);
        Assert.False(conflict.Success);
        Assert.Equal(DeviceCommandError.Conflict, conflict.Error);
        Assert.Equal(3, device.Revision);

        var tokenless = await service.UpdateAsync(db, device, default, expectedRevision: null);
        Assert.NotEqual(DeviceCommandError.Conflict, tokenless.Error);

        device.Revision = 3;
        var staleZero = await service.UpdateAsync(db, device, default, expectedRevision: 0);
        Assert.NotEqual(DeviceCommandError.Conflict, staleZero.Error);

        device.Revision = 3;
        var matched = await service.UpdateAsync(db, device, default, expectedRevision: 3);
        Assert.NotEqual(DeviceCommandError.Conflict, matched.Error);
    }

    private sealed class EmptySnapshot : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => [];
        public IReadOnlyList<Tag> Tags => [];
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag tag) { tag = null!; return false; }
        public bool TryGetDevice(int deviceId, out Device device) { device = null!; return false; }
    }

    private sealed class Collector : ICollectorControl
    {
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => [];
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default)
            => Task.FromResult(new CommandExecutionResult(false, "unused"));
        public void RequestReload() { }
    }

    private sealed class Drivers : IProtocolDriverAvailability
    {
        public bool IsDriverAvailable(ProtocolType protocol) => true;
    }

    private sealed class Simulations : ISimulationRuntime
    {
        public SimulationSnapshot Snapshot(Device device, SimulationDeviceOptions options, IReadOnlyList<Tag> tags) => new(device.Id, false, default, 0, []);
        public void Force(int deviceId, int tagId, double value) { }
        public bool Release(int deviceId, int tagId) => false;
        public int ReleaseAll(int deviceId) => 0;
        public void InjectFault(int deviceId, SimulationFaultKind kind, int durationSec) { }
        public void ClearFault(int deviceId) { }
        public void Remove(int deviceId) { }
    }
}
