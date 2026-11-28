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

        paths.Path = AcquisitionPathKind.Standby;
        Assert.Equal(1, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        var closed = Assert.Single(await DeviceAlarmsAsync(rig));
        Assert.Equal(alarm.Id, closed.Id);
        Assert.NotNull(closed.EndTs);
        Assert.Contains("采集位置已变更", closed.EndReason);
    }

    private sealed class MutablePaths : IDeviceAcquisitionPaths
    {
        public AcquisitionPathKind Path { get; set; } = AcquisitionPathKind.Primary;
        public DeviceAcquisitionRoute Resolve(Device device) =>
            Path == AcquisitionPathKind.Standby ? DeviceAcquisitionRules.StandbyOf(device) : DeviceAcquisitionRules.PrimaryOf(device);
        public Device Apply(Device device) => DeviceAcquisitionRules.Apply(device, Resolve(device));
        public bool HasStandby(Device device) => DeviceAcquisitionRules.HasStandby(device);
        public AcquisitionPathKind Current(int deviceId) => Path;
        public event Action<int, AcquisitionPathKind, string>? PathChanged { add { } remove { } }
    }
}
