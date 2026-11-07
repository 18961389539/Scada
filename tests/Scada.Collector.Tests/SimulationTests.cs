using Scada.Collector.Simulation;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Simulation;

namespace Scada.Collector.Tests;

public class SimulationTests
{
    private static Device Device(bool enabled = true, string options = "") => new()
    {
        Id = 1, Name = "sim", Protocol = ProtocolType.ModbusTcp, Host = "", Port = 0,
        PollIntervalMs = 1000, TimeoutMs = 3000, SimulationEnabled = enabled, SimulationOptions = options
    };

    private static Tag Tag(int id, TagDataType type, double? min = null, double? max = null) => new()
    {
        Id = id, DeviceId = 1, Name = $"tag{id}", Kind = TagKind.Physical, Area = TagArea.HoldingRegister,
        Address = id, DataType = type, MinValue = min, MaxValue = max
    };

    [Fact]
    public void SimulationDeviceSkipsProtocolConnectionValidationButRejectsGatewayAssignment()
    {
        var device = Device();
        Assert.Null(ConfigurationValidator.Device(device));
        device.GatewayId = 2;
        Assert.Equal("仿真设备只能由中心本机采集，不能分配给网关", ConfigurationValidator.Device(device));
    }

    [Fact]
    public void SimulationOptionsValidatePeriodRangeAndNoise()
    {
        var device = Device(options: "{\"periodSec\":0}");
        Assert.Contains("周期", ConfigurationValidator.Device(device));
        device.SimulationOptions = "{\"min\":0}";
        Assert.Contains("必须同时", ConfigurationValidator.Device(device));
        device.SimulationOptions = "{\"min\":10,\"max\":1}";
        Assert.Contains("最大值", ConfigurationValidator.Device(device));
        device.SimulationOptions = "{\"noisePercent\":80}";
        Assert.Contains("噪声", ConfigurationValidator.Device(device));
        device.SimulationOptions = "{\"periodSec\":60,\"min\":0,\"max\":100,\"noisePercent\":5}";
        Assert.Null(ConfigurationValidator.Device(device));
    }

    [Fact]
    public void RuntimeGeneratesTypedValuesAndHonorsForceRelease()
    {
        var runtime = new SimulationRuntime();
        var device = Device();
        var tags = new[] { Tag(1, TagDataType.Bool), Tag(2, TagDataType.Int32, 0, 100), Tag(3, TagDataType.Float, 0, 50) };
        var options = new SimulationDeviceOptions { Waveform = SimulationWaveform.Sine, PeriodSec = 60, Min = 0, Max = 100 };

        var values = runtime.Read(device, options, tags);
        Assert.All(values, value => Assert.Equal(TagQuality.Good, value.Quality));
        Assert.InRange(values[0].Value, 0, 1);
        Assert.InRange(values[1].Value, 0, 100);
        Assert.Equal(Math.Round(values[1].Value), values[1].Value);
        Assert.InRange(values[2].Value, 0, 50);

        runtime.Write(device.Id, 2, 42);
        Assert.Equal(42, runtime.Read(device, options, tags).Single(value => value.TagId == 2).Value);
        Assert.True(runtime.Release(device.Id, 2));
        Assert.NotEqual(42, runtime.Read(device, options, tags).Single(value => value.TagId == 2).Value);
    }

    [Fact]
    public void RuntimeInjectsCommunicationAndQualityFaults()
    {
        var runtime = new SimulationRuntime();
        var device = Device();
        var tags = new[] { Tag(1, TagDataType.Float, 0, 100) };
        var options = new SimulationDeviceOptions();

        runtime.InjectFault(device.Id, SimulationFaultKind.CommFailure, 60);
        Assert.Throws<IOException>(() => runtime.Read(device, options, tags));
        runtime.ClearFault(device.Id);

        runtime.InjectFault(device.Id, SimulationFaultKind.BadQuality, 60);
        var bad = runtime.Read(device, options, tags);
        Assert.Equal(TagQuality.Bad, bad.Single().Quality);
        Assert.Contains("坏质量", bad.Single().Error);
        runtime.ClearFault(device.Id);

        runtime.InjectFault(device.Id, SimulationFaultKind.Frozen, 60);
        var first = runtime.Read(device, options, tags).Single().Value;
        var second = runtime.Read(device, options, tags).Single().Value;
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DriverReadsWritesAndReadsBackSimulatedValues()
    {
        var runtime = new SimulationRuntime();
        var driver = new SimulationDeviceDriver(runtime);
        var device = Device();
        var tag = Tag(7, TagDataType.Float, 0, 100);
        tag.Writable = true;
        var plan = new ReadPlan { DeviceId = device.Id, Tags = [tag] };

        await driver.ConnectAsync(device);
        var initial = (await driver.ReadAsync(device, plan)).Single();
        Assert.Equal(TagQuality.Good, initial.Quality);
        await driver.WriteAsync(device, tag, 66);
        var written = (await driver.ReadAsync(device, plan)).Single();
        Assert.Equal(66, written.Value);
        driver.Disconnect();
        Assert.False(driver.IsConnected);
    }
}
