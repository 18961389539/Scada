using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Acquisition;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Engine.Acquisition;

namespace Scada.Engine.Tests;

public sealed class DeviceAcquisitionPathTests
{
    private static Device Device(int id = 1, int? gateway = null, string host = "10.0.0.1", int port = 502, int? standbyGateway = null, string? standbyHost = "10.0.0.2", int? standbyPort = 502) => new()
    {
        Id = id, Name = "泵", GatewayId = gateway, Host = host, Port = port,
        StandbyGatewayId = standbyGateway, StandbyHost = standbyHost, StandbyPort = standbyPort,
        FailoverHoldMs = 10_000, SwitchbackHoldMs = 30_000, Enabled = true
    };

    [Fact]
    public void StandbyHostIsADistinctPathAndSimulationCannotUseIt()
    {
        var device = Device();
        Assert.True(DeviceAcquisitionRules.HasStandby(device));
        Assert.Null(DeviceAcquisitionRules.Validate(device));
        var standby = DeviceAcquisitionRules.StandbyOf(device);
        Assert.Equal("10.0.0.2", standby.Host);
        Assert.Equal(AcquisitionPathKind.Standby, standby.Path);
        Assert.NotEqual(DeviceAcquisitionRules.PrimaryOf(device).Host, standby.Host);

        device.StandbyHost = device.Host; device.StandbyPort = device.Port;
        Assert.Equal("备路径不能与主路径相同", DeviceAcquisitionRules.Validate(device));

        var sim = Device(); sim.SimulationEnabled = true;
        Assert.Equal("仿真设备不能配置采集备路径", DeviceAcquisitionRules.Validate(sim));

        var incomplete = Device(standbyHost: null, standbyPort: null, standbyGateway: 2);
        Assert.Equal("备路径需要备用通道，或备用主机和端口", DeviceAcquisitionRules.Validate(incomplete));
        incomplete.StandbyHost = "10.0.0.8"; incomplete.StandbyPort = 502;
        Assert.Null(DeviceAcquisitionRules.Validate(incomplete));
        var applied = DeviceAcquisitionRules.Apply(incomplete, DeviceAcquisitionRules.StandbyOf(incomplete));
        Assert.Equal(2, applied.GatewayId);
        Assert.Equal("10.0.0.8", applied.Host);
        Assert.Equal(502, applied.Port);
        Assert.Equal("10.0.0.1", incomplete.Host);
    }

    [Fact]
    public void EvaluateSwitchesToStandbyAfterHoldAndBackAfterLongerHold()
    {
        var device = Device();
        var state = new DeviceAcquisitionState { DeviceId = 1, ActivePath = AcquisitionPathKind.Primary };
        Assert.Null(DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: true, primaryHealthy: true, now: 1_000));
        Assert.Null(DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: false, primaryHealthy: false, now: 2_000));
        Assert.Equal(2_000, state.ActiveUnhealthySince);
        Assert.Null(DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: false, primaryHealthy: false, now: 11_000));
        var failover = DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: false, primaryHealthy: false, now: 12_000);
        Assert.NotNull(failover);
        Assert.Equal(AcquisitionPathKind.Standby, failover!.Value.Path);
        Assert.Contains("切到备路径", failover.Value.Reason);

        state.ActivePath = AcquisitionPathKind.Standby;
        state.ActiveUnhealthySince = null;
        state.PrimaryHealthySince = null;
        Assert.Null(DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: true, primaryHealthy: true, now: 20_000));
        Assert.Equal(20_000, state.PrimaryHealthySince);
        Assert.Null(DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: true, primaryHealthy: true, now: 49_000));
        var back = DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: true, primaryHealthy: true, now: 50_000);
        Assert.NotNull(back);
        Assert.Equal(AcquisitionPathKind.Primary, back!.Value.Path);
        Assert.Contains("自动切回", back.Value.Reason);
    }
    [Fact]
    public void ManualLockSuppressesAutomaticSwitchbackUntilResumed()
    {
        var device = Device();
        var state = new DeviceAcquisitionState
        {
            DeviceId = 1, ActivePath = AcquisitionPathKind.Standby, ManualLock = true, PrimaryHealthySince = 20_000
        };
        Assert.Null(DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: true, primaryHealthy: true, now: 60_000));

        state.ManualLock = false;
        state.PrimaryHealthySince = null;
        Assert.Null(DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: true, primaryHealthy: true, now: 70_000));
        Assert.Equal(70_000, state.PrimaryHealthySince);
        Assert.Null(DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: true, primaryHealthy: true, now: 99_000));
        var back = DeviceAcquisitionPathService.Evaluate(device, state, activeHealthy: true, primaryHealthy: true, now: 100_000);
        Assert.NotNull(back);
        Assert.Equal(AcquisitionPathKind.Primary, back!.Value.Path);
    }

    [Fact]
    public void StandbyChannelEndpointIsAuthoritativeOnApply()
    {
        var device = new Device
        {
            Id = 1, Name = "总线", Protocol = ProtocolType.ModbusRtu,
            ChannelId = 1, SerialPort = "COM1", BaudRate = 9600, DataBits = 8, TimeoutMs = 3000,
            StandbyChannelId = 2, FailoverHoldMs = 10_000, SwitchbackHoldMs = 30_000, Enabled = true
        };
        var before = DeviceAcquisitionRules.Apply(device, DeviceAcquisitionRules.StandbyOf(device));
        Assert.Equal("COM1", before.SerialPort);
        Assert.Equal(3000, before.TimeoutMs);

        var standby = new AcquisitionChannel
        {
            Id = 2, Name = "备总线", Protocol = ProtocolType.ModbusRtu,
            SerialPort = "COM8", BaudRate = 19200, DataBits = 8, TimeoutMs = 800
        };
        DeviceAcquisitionRules.StampStandbyFromChannel(device, standby);
        Assert.Null(DeviceAcquisitionRules.Validate(device));
        Assert.Equal("COM8", device.StandbySerialPort);
        Assert.Equal(19200, device.StandbyBaudRate);
        Assert.Equal(800, device.StandbyTimeoutMs);

        var applied = DeviceAcquisitionRules.Apply(device, DeviceAcquisitionRules.StandbyOf(device));
        Assert.Equal(2, applied.ChannelId);
        Assert.Equal("COM8", applied.SerialPort);
        Assert.Equal(19200, applied.BaudRate);
        Assert.Equal(800, applied.TimeoutMs);
        Assert.Equal("COM1", device.SerialPort);
        Assert.Equal(3000, device.TimeoutMs);
    }

    [Fact]
    public async Task CurrentReloadsActivePathWhenRoleGenerationChanges()
    {
        using var connection = TestRuntime.Open();
        var factory = TestRuntime.Factory(connection);
        var role = new MutableRole();
        var device = Device();
        var snapshot = new PathSnapshot(device);
        await using (var runtime = await factory.CreateDbContextAsync())
        {
            runtime.DeviceAcquisitionStates.Add(new DeviceAcquisitionState
            {
                DeviceId = device.Id, ActivePath = AcquisitionPathKind.Primary, ChangedTs = 1
            });
            await runtime.SaveChangesAsync();
        }

        var service = new DeviceAcquisitionPathService(factory, snapshot, new EmptyCache(), NullLogger<DeviceAcquisitionPathService>.Instance, role);
        await service.LoadAsync(CancellationToken.None);
        Assert.Equal(AcquisitionPathKind.Primary, service.Current(device.Id));

        await using (var runtime = await factory.CreateDbContextAsync())
        {
            var state = await runtime.DeviceAcquisitionStates.SingleAsync();
            state.ActivePath = AcquisitionPathKind.Standby;
            await runtime.SaveChangesAsync();
        }

        Assert.Equal(AcquisitionPathKind.Primary, service.Current(device.Id));
        role.Bump();
        Assert.Equal(AcquisitionPathKind.Standby, service.Current(device.Id));
    }

    [Fact]
    public void RouteHealthUsesMajorityGoodSamples()
    {
        Assert.False(DeviceAcquisitionPathService.IsSampleHealthy(0, 0));
        Assert.False(DeviceAcquisitionPathService.IsSampleHealthy(4, 1));
        Assert.True(DeviceAcquisitionPathService.IsSampleHealthy(4, 2));
        Assert.True(DeviceAcquisitionPathService.IsSampleHealthy(1, 1));
    }

    private sealed class MutableRole : INodeRole
    {
        public bool IsActive => true;
        public long Generation { get; private set; }
        public void Bump() => Generation++;
    }

    private sealed class PathSnapshot(params Device[] devices) : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices { get; } = devices;
        public IReadOnlyList<Tag> Tags { get; } = [];
        public IReadOnlyList<AlarmRule> AlarmRules { get; } = [];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag tag) { tag = null!; return false; }
        public bool TryGetDevice(int deviceId, out Device device)
        {
            device = Devices.FirstOrDefault(x => x.Id == deviceId)!;
            return device is not null;
        }
    }

    private sealed class EmptyCache : IRealtimeCache
    {
        public void Set(TagValue value) { }
        public TagValue? Get(int tagId) => null;
        public IReadOnlyList<TagValue> GetMany(IEnumerable<int> tagIds) => [];
        public IReadOnlyDictionary<int, TagValue> Snapshot() => new Dictionary<int, TagValue>();
    }
}
