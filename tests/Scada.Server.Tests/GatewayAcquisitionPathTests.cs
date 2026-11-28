using Microsoft.EntityFrameworkCore;
using Scada.Core.Acquisition;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public partial class GatewayCenterTests
{
    [Fact]
    public async Task CommandsAreQueuedToTheActiveStandbyGatewayNotTheConfiguredPrimary()
    {
        var paths = new MutablePaths();
        GatewayCaller? standbyCaller = null;
        await using var rig = await Rig.CreateAsync(paths: paths, configure: async r =>
        {
            var identity = await r.Service.CreateAsync("standby-edge", 1, "test", default);
            standbyCaller = new GatewayCaller(identity.Id, GatewayCenterService.TokenHash(identity.Token));
            await using var db = r.Factory.CreateDbContext();
            var device = await db.Devices.SingleAsync(x => x.Id == r.DeviceId);
            device.StandbyGatewayId = identity.Id;
            device.StandbyHost = "10.0.0.8";
            device.StandbyPort = 502;
            await db.SaveChangesAsync();
        });
        Assert.NotNull(standbyCaller);
        await rig.EnableCommandsAsync();
        paths.NotifyDeviceId = rig.DeviceId;
        paths.Path = AcquisitionPathKind.Standby;
        var id = Guid.NewGuid();
        var actor = new CollectorCommandContext(id, rig.OperatorId, "operator");
        var pending = rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 42, actor, default);
        GatewayControlCommand? row = null;
        for (var i = 0; i < 40 && row is null; i++)
        {
            await using var runtime = rig.Runtime.CreateDbContext();
            row = await runtime.GatewayCommands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
            if (row is null) await Task.Delay(50);
        }
        Assert.NotNull(row);
        Assert.Equal(standbyCaller!.Id, row!.GatewayId);
        Assert.NotEqual(rig.Caller.Id, row.GatewayId);
        try { await pending.WaitAsync(TimeSpan.FromMilliseconds(200)); } catch (TimeoutException) { }
    }

    [Fact]
    public async Task DeviceOfflineAlarmFollowsTheActivePathAndClosesWhenThePathMoves()
    {
        var paths = new MutablePaths();
        await using var rig = await Rig.CreateAsync(paths: paths, configure: async r =>
        {
            var identity = await r.Service.CreateAsync("standby-edge", 1, "test", default);
            await using var db = r.Factory.CreateDbContext();
            var device = await db.Devices.SingleAsync(x => x.Id == r.DeviceId);
            device.StandbyGatewayId = identity.Id;
            device.StandbyHost = "10.0.0.8";
            device.StandbyPort = 502;
            await db.SaveChangesAsync();
        });

        var alarm = await RaiseRemoteOfflineAsync(rig);
        Assert.Equal(rig.Caller.Id, alarm.GatewayId);

        paths.NotifyDeviceId = rig.DeviceId;
        paths.Path = AcquisitionPathKind.Standby;
        Assert.Equal(1, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        var closed = Assert.Single(await DeviceAlarmsAsync(rig));
        Assert.Equal(alarm.Id, closed.Id);
        Assert.NotNull(closed.EndTs);
        Assert.Contains("采集位置已变更", closed.EndReason);
    }

    [Fact]
    public async Task PathSwitchToAConnectedStandbyDoesNotRaiseANewOfflineAlarmWhileConfigIsPending()
    {
        var paths = new MutablePaths();
        GatewayCaller? standbyCaller = null;
        await using var rig = await Rig.CreateAsync(paths: paths, configure: async r =>
        {
            var identity = await r.Service.CreateAsync("standby-edge", 1, "test", default);
            standbyCaller = new GatewayCaller(identity.Id, GatewayCenterService.TokenHash(identity.Token));
            await using var db = r.Factory.CreateDbContext();
            var device = await db.Devices.SingleAsync(x => x.Id == r.DeviceId);
            device.StandbyGatewayId = identity.Id;
            device.StandbyHost = "10.0.0.8";
            device.StandbyPort = 502;
            await db.SaveChangesAsync();
        });
        Assert.NotNull(standbyCaller);
        var session = await rig.Service.HelloAsync(standbyCaller!, new(1, Guid.NewGuid(), Guid.NewGuid(), "standby"), default);
        var configuration = await rig.Service.ConfigurationAsync(standbyCaller!, session.SessionId, default);
        await rig.Service.HeartbeatAsync(standbyCaller!, session.SessionId, new(configuration.Revision, 0, 0, null, null), default);

        var alarm = await RaiseRemoteOfflineAsync(rig);
        paths.NotifyDeviceId = rig.DeviceId;
        paths.Path = AcquisitionPathKind.Standby;
        await rig.Service.ReconcileDeviceAlarmsAsync(default);
        Assert.All(await DeviceAlarmsAsync(rig), x => Assert.NotNull(x.EndTs));

        await rig.Service.HeartbeatAsync(standbyCaller!, session.SessionId, new(configuration.Revision, 0, 0, null, null), default);
        rig.Clock.Now += 20_000;
        await rig.Service.ReconcileDeviceAlarmsAsync(default);
        Assert.DoesNotContain(await DeviceAlarmsAsync(rig), x => x.EndTs is null);
        Assert.Equal(alarm.Id, Assert.Single(await DeviceAlarmsAsync(rig)).Id);
    }

    [Fact]
    public async Task PathChangeSettlesAuthorizedCommandsAsUnknownAndUnsentCommandsAsNotSent()
    {
        var paths = new MutablePaths();
        await using var rig = await Rig.CreateAsync(paths: paths, configure: async r =>
        {
            var identity = await r.Service.CreateAsync("standby-edge", 1, "test", default);
            await using var db = r.Factory.CreateDbContext();
            var device = await db.Devices.SingleAsync(x => x.Id == r.DeviceId);
            device.StandbyGatewayId = identity.Id;
            device.StandbyHost = "10.0.0.8";
            device.StandbyPort = 502;
            await db.SaveChangesAsync();
        });
        await rig.EnableCommandsAsync();

        var authorizedId = Guid.NewGuid();
        var authorizedPending = rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 7, new(authorizedId, rig.OperatorId, "operator"), default);
        await rig.Service.CommandOffersAsync(rig.Caller, rig.Session.SessionId, default);
        await rig.Service.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, authorizedId, default);

        var queuedId = Guid.NewGuid();
        var queuedPending = rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 8, new(queuedId, rig.OperatorId, "operator"), default);
        for (var i = 0; i < 40; i++)
        {
            await using var runtime = rig.Runtime.CreateDbContext();
            if (await runtime.GatewayCommands.AsNoTracking().AnyAsync(x => x.Id == queuedId)) break;
            await Task.Delay(50);
        }

        paths.NotifyDeviceId = rig.DeviceId;
        paths.Path = AcquisitionPathKind.Standby;
        await rig.Service.SettleCommandsAfterPathChangeAsync(rig.DeviceId);

        Assert.Equal(GatewayCommandOutcome.Unknown, (await authorizedPending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Equal(GatewayCommandOutcome.NotSent, (await queuedPending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.Contains("路径已变更", (await rig.Service.CommandStatusAsync(authorizedId, rig.OperatorId, false, default))!.Result!.Message);
    }

    [Fact]
    public async Task GatewayListDoesNotThrowWhenALocalPrimaryHasAStandbyGateway()
    {
        await using var rig = await Rig.CreateAsync(configure: async r =>
        {
            var identity = await r.Service.CreateAsync("standby-edge", 1, "test", default);
            await using var db = r.Factory.CreateDbContext();
            var device = await db.Devices.SingleAsync(x => x.Id == r.DeviceId);
            device.StandbyGatewayId = identity.Id;
            device.GatewayId = null;
            device.StandbyHost = "10.0.0.8";
            device.StandbyPort = 502;
            await db.SaveChangesAsync();
        });

        var list = await rig.Service.ListAsync(default);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task ManagedDevicesListsOnlyTheActivePathAndMarksAConfiguredStandby()
    {
        var paths = new MutablePaths();
        int standbyId = 0;
        await using var rig = await Rig.CreateAsync(paths: paths, configure: async r =>
        {
            var identity = await r.Service.CreateAsync("standby-edge", 1, "test", default);
            standbyId = identity.Id;
            await using var db = r.Factory.CreateDbContext();
            var device = await db.Devices.SingleAsync(x => x.Id == r.DeviceId);
            device.StandbyGatewayId = identity.Id;
            device.StandbyHost = "10.0.0.8";
            device.StandbyPort = 502;
            await db.SaveChangesAsync();
        });

        var primary = Assert.Single(await rig.Service.ManagedDevicesAsync(rig.Caller.Id, default));
        Assert.Equal(rig.DeviceId, primary.Id);
        Assert.False(primary.ConfiguredStandby);
        Assert.Empty(await rig.Service.ManagedDevicesAsync(standbyId, default));

        paths.NotifyDeviceId = rig.DeviceId;
        paths.Path = AcquisitionPathKind.Standby;
        Assert.Empty(await rig.Service.ManagedDevicesAsync(rig.Caller.Id, default));
        var standby = Assert.Single(await rig.Service.ManagedDevicesAsync(standbyId, default));
        Assert.Equal(rig.DeviceId, standby.Id);
        Assert.True(standby.ConfiguredStandby);
    }

    private sealed class MutablePaths : IDeviceAcquisitionPaths
    {
        private AcquisitionPathKind _path = AcquisitionPathKind.Primary;
        public int NotifyDeviceId { get; set; }
        public AcquisitionPathKind Path
        {
            get => _path;
            set
            {
                if (_path == value) return;
                _path = value;
                if (NotifyDeviceId > 0)
                    PathChanged?.Invoke(NotifyDeviceId, value, "测试切换采集路径");
            }
        }
        public DeviceAcquisitionRoute Resolve(Device device) =>
            Path == AcquisitionPathKind.Standby ? DeviceAcquisitionRules.StandbyOf(device) : DeviceAcquisitionRules.PrimaryOf(device);
        public Device Apply(Device device) => DeviceAcquisitionRules.Apply(device, Resolve(device));
        public bool HasStandby(Device device) => DeviceAcquisitionRules.HasStandby(device);
        public AcquisitionPathKind Current(int deviceId) => Path;
        public event Action<int, AcquisitionPathKind, string>? PathChanged;
    }
}
