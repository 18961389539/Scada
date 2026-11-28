using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core;
using Scada.Core.Acquisition;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Server.Endpoints;
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
        // 切路径前先续租，避免窗口加上离线延时后租约先到期，改报「租约已过期」。
        await rig.Service.HeartbeatAsync(standbyCaller!, session.SessionId, new(configuration.Revision, 0, 0, null, null), default);
        paths.NotifyDeviceId = rig.DeviceId;
        paths.Path = AcquisitionPathKind.Standby;
        await rig.Service.ReconcileDeviceAlarmsAsync(default);
        Assert.All(await DeviceAlarmsAsync(rig), x => Assert.NotNull(x.EndTs));

        // 宽限窗口内仍不新升起离线。
        rig.Clock.Now += 5_000;
        await rig.Service.ReconcileDeviceAlarmsAsync(default);
        Assert.DoesNotContain(await DeviceAlarmsAsync(rig), x => x.EndTs is null);

        // 窗口过后先记下离线起点，再经过默认延时，配置仍未确认时必须按「尚未确认最新配置」报警。
        rig.Clock.Now += 20_000;
        await rig.Service.ReconcileDeviceAlarmsAsync(default);
        Assert.DoesNotContain(await DeviceAlarmsAsync(rig), x => x.EndTs is null);
        rig.Clock.Now += 3_000;
        await rig.Service.ReconcileDeviceAlarmsAsync(default);
        var open = Assert.Single(await DeviceAlarmsAsync(rig), x => x.EndTs is null);
        Assert.NotEqual(alarm.Id, open.Id);
        Assert.Contains("尚未确认最新采集配置", open.Message);
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

    [Fact]
    public async Task AssignmentBlockWaitsForTheActiveStandbyGatewayNotTheConfiguredPrimary()
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
        paths.NotifyDeviceId = rig.DeviceId;
        paths.Path = AcquisitionPathKind.Standby;

        await using (var db = rig.Factory.CreateDbContext())
        {
            var device = await db.Devices.SingleAsync(x => x.Id == rig.DeviceId);
            device.Enabled = false;
            device.DisabledSinceTs = rig.Clock.Now;
            await db.SaveChangesAsync();
        }

        var waiting = await rig.Service.AssignmentInfoAsync(rig.DeviceId, default);
        Assert.False(waiting.CanMove);
        Assert.Contains("当前活动采集网关", waiting.BlockReason!);
        Assert.Contains($"网关 #{standbyCaller!.Id}", waiting.BlockReason!);
        Assert.Equal(rig.Caller.Id, waiting.GatewayId);
        Assert.Equal(standbyCaller.Id, waiting.ActiveGatewayId);

        var primaryStopped = await rig.Service.ConfigurationAsync(rig.Caller, rig.Session.SessionId, default);
        await rig.Service.HeartbeatAsync(rig.Caller, rig.Session.SessionId, new(primaryStopped.Revision, 0, 0, null, null), default);
        waiting = await rig.Service.AssignmentInfoAsync(rig.DeviceId, default);
        Assert.False(waiting.CanMove);

        var standbyStopped = await rig.Service.ConfigurationAsync(standbyCaller!, session.SessionId, default);
        await rig.Service.HeartbeatAsync(standbyCaller!, session.SessionId, new(standbyStopped.Revision, 0, 0, null, null), default);
        var ready = await rig.Service.AssignmentInfoAsync(rig.DeviceId, default);
        Assert.True(ready.CanMove);
        Assert.Equal(rig.Caller.Id, ready.GatewayId);
        await rig.Service.AssignAsync(rig.DeviceId, null, 1, "test", default, new(ready.GatewayId, ready.DisabledSinceTs));
        Assert.Null((await rig.Service.AssignmentInfoAsync(rig.DeviceId, default)).GatewayId);
    }

    [Fact]
    public async Task PathChangeSettlesUnsentEngineeringAndMarksOfferedEngineeringUnknown()
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
        await rig.Service.HelloAsync(rig.Caller, new(1, rig.Instance, rig.Stream, "engineering-test", [GatewayProtocol.EngineeringCapability]), default);
        int userId;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var user = new User { UserName = "path-engineer", Role = UserRole.Admin, PasswordHash = "test-only" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var queued = rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Diagnostics, new { }, userId, default);
        paths.NotifyDeviceId = rig.DeviceId;
        paths.Path = AcquisitionPathKind.Standby;
        await rig.Service.SettleEngineeringAfterPathChangeAsync(rig.DeviceId);
        var queuedEx = await Assert.ThrowsAsync<GatewayRequestException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(409, queuedEx.StatusCode);
        Assert.Contains("未下发", queuedEx.Message);

        paths.Path = AcquisitionPathKind.Primary;
        var offered = rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Diagnostics, new { page = 1 }, userId, default);
        var offer = await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default);
        Assert.NotNull(offer.Request);
        paths.Path = AcquisitionPathKind.Standby;
        await rig.Service.SettleEngineeringAfterPathChangeAsync(rig.DeviceId);
        var offeredEx = await Assert.ThrowsAsync<GatewayRequestException>(() => offered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(409, offeredEx.StatusCode);
        Assert.Contains("结果未知", offeredEx.Message);
        Assert.Null((await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request);
    }

    [Fact]
    public async Task LocalActivePathRaisesOfficialOfflineWhenLocalCollectionIsDisabled()
    {
        var paths = new MutablePaths();
        await using var rig = await Rig.CreateAsync(paths: paths, scadaSettings: Options.Create(new ScadaSettings { LocalCollectionEnabled = false }), configure: async r =>
        {
            await using var db = r.Factory.CreateDbContext();
            var device = await db.Devices.SingleAsync(x => x.Id == r.DeviceId);
            device.StandbyHost = "10.0.0.8";
            device.StandbyPort = 502;
            await db.SaveChangesAsync();
        });
        paths.NotifyDeviceId = rig.DeviceId;
        paths.Path = AcquisitionPathKind.Standby;
        await using (var config = rig.Factory.CreateDbContext())
            Assert.True(paths.Resolve(await config.Devices.SingleAsync(x => x.Id == rig.DeviceId)).IsLocal);
        Assert.Equal(1, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        // DeviceAlarmsAsync 只看网关设备离线；本机活动路径的官方离线 GatewayId 为空。
        await using var runtime = rig.Runtime.CreateDbContext();
        var open = Assert.Single(await runtime.AlarmEvents.AsNoTracking().Where(x => x.DeviceId == rig.DeviceId && x.EndTs == null).ToListAsync());
        Assert.Null(open.GatewayId);
        Assert.Contains(LocalCollection.ActiveLocalOfflineReason, open.Message);
    }

    [Fact]
    public void DeviceListFilterUsesTheActivePathAndMarksConfiguredStandby()
    {
        var paths = new MutablePaths();
        var primary = new Device { Id = 1, Name = "主", GatewayId = 10, StandbyGatewayId = 20, StandbyHost = "10.0.0.8", StandbyPort = 502 };
        var onlyStandby = new Device { Id = 2, Name = "备", GatewayId = 11, StandbyGatewayId = 10, StandbyHost = "10.0.0.9", StandbyPort = 502 };
        var onPrimary = DeviceEndpoints.FilterByActiveGateway([primary, onlyStandby], paths, 10);
        Assert.Equal(primary.Id, Assert.Single(onPrimary).Id);
        Assert.False(onPrimary[0].ConfiguredStandby);
        Assert.Equal(10, onPrimary[0].ActiveGatewayId);

        paths.Path = AcquisitionPathKind.Standby;
        var onStandby = DeviceEndpoints.FilterByActiveGateway([primary, onlyStandby], paths, 10);
        var marked = Assert.Single(onStandby);
        Assert.Equal(onlyStandby.Id, marked.Id);
        Assert.True(marked.ConfiguredStandby);
        Assert.Equal(10, marked.ActiveGatewayId);
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
