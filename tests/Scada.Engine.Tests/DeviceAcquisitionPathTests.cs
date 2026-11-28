using Scada.Core.Acquisition;
using Scada.Core.Entities;
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
}
