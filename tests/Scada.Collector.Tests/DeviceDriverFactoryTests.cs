using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Collector.Drivers;
using Scada.Collector.Simulation;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Core.Protocols;

namespace Scada.Collector.Tests;

public class DeviceDriverFactoryTests
{
    private sealed class PlainCredentials : IDeviceCredentials
    {
        public string Protect(string password) => password;
        public string Unprotect(string protectedPassword) => protectedPassword;
    }

    private static DeviceDriverFactory Factory()
    {
        var credentials = new PlainCredentials();
        var plugins = new DriverPluginCatalog(
            Options.Create(new ScadaSettings()),
            credentials,
            NullLogger<DriverPluginCatalog>.Instance);
        return new DeviceDriverFactory(
            NullLoggerFactory.Instance,
            null!,
            credentials,
            new SimulationRuntime(),
            plugins);
    }

    private static Device Device(ProtocolType protocol, bool simulation = false, int? gatewayId = null) => new()
    {
        Id = 1, Name = "device", Protocol = protocol, Host = "127.0.0.1", Port = 502,
        SimulationEnabled = simulation, GatewayId = gatewayId
    };

    [Fact]
    public void SimulationDevicesUseTheSimulationDriverAndRejectGatewayAssignment()
    {
        var factory = Factory();
        Assert.IsType<SimulationDeviceDriver>(factory.Create(Device(ProtocolType.ModbusTcp, simulation: true)));

        var assigned = Assert.Throws<InvalidOperationException>(
            () => factory.Create(Device(ProtocolType.ModbusTcp, simulation: true, gatewayId: 2)));
        Assert.Contains("仿真设备不能由网关采集", assigned.Message);
    }

    [Fact]
    public void RemoteDevicesAreRejectedBeforeAnyDriverIsCreated()
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => Factory().Create(Device(ProtocolType.ModbusTcp, gatewayId: 2)));
        Assert.Contains("远程设备必须由指定网关执行采集", failure.Message);
    }

    [Fact]
    public void UnavailableProtocolsAndUnknownValuesProduceClearErrors()
    {
        var factory = Factory();

        var missingPlugin = Assert.Throws<NotSupportedException>(
            () => factory.Create(Device(ProtocolType.Iec61850)));
        Assert.Equal(Iec61850Spec.NotInstalledMessage, missingPlugin.Message);

        var unknown = Assert.Throws<InvalidOperationException>(
            () => factory.Create(Device((ProtocolType)9999)));
        Assert.Contains("不支持设备协议", unknown.Message);
    }

    [Fact]
    public void BuiltInProtocolsResolveToTheirDriverImplementations()
    {
        var factory = Factory();
        Assert.IsType<Modbus.ModbusTcpDriver>(factory.Create(Device(ProtocolType.ModbusTcp)));
        Assert.IsType<Modbus.ModbusRtuDriver>(factory.Create(Device(ProtocolType.ModbusRtu)));
        Assert.IsType<EtherNetIp.EtherNetIpDriver>(factory.Create(Device(ProtocolType.EtherNetIp)));
        Assert.IsType<Simulation.SimulationDeviceDriver>(factory.Create(Device(ProtocolType.SiemensS7, simulation: true)));
    }

    [Fact]
    public void SnapshotListsEveryBuiltInWithoutPretendingItIsAPlugin()
    {
        var snapshot = Factory().Snapshot();
        Assert.False(snapshot.PluginLoadingEnabled);
        Assert.Empty(snapshot.Failures);
        Assert.Equal(DeviceDriverFactory.BuiltInProtocols.Count, snapshot.Drivers.Count);
        Assert.All(snapshot.Drivers, driver => Assert.False(driver.IsPlugin));
        Assert.All(snapshot.Drivers, driver => Assert.Equal("built-in", driver.Version));
    }
}