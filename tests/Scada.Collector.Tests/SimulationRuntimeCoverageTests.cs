using Scada.Collector.Simulation;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Simulation;

namespace Scada.Collector.Tests;

public class SimulationRuntimeCoverageTests
{
    private static Scada.Core.Entities.Device Device(int id = 1) => new()
    {
        Id = id, Name = "sim", Protocol = ProtocolType.ModbusTcp,
        PollIntervalMs = 1000, TimeoutMs = 3000, SimulationEnabled = true
    };

    private static Scada.Core.Entities.Tag Tag(int id, TagDataType type, double min, double max) => new()
    {
        Id = id, DeviceId = 1, Name = $"tag{id}", Kind = TagKind.Physical,
        Area = TagArea.HoldingRegister, Address = id, DataType = type, MinValue = min, MaxValue = max
    };

    private static SimulationDeviceOptions Options(SimulationWaveform waveform = SimulationWaveform.Sine, double noise = 0) => new()
    {
        Waveform = waveform, PeriodSec = 1, Min = 0, Max = 100, NoisePercent = noise
    };

    [Fact]
    public void SnapshotReportsFaultQualityForcedStateAndExpiry()
    {
        var runtime = new SimulationRuntime();
        var device = Device();
        var tags = new[] { Tag(1, TagDataType.Float, 0, 100), Tag(2, TagDataType.Bool, 0, 1) };

        var normal = runtime.Snapshot(device, Options(), tags);
        Assert.True(normal.Enabled);
        Assert.Equal(SimulationFaultKind.None, normal.Fault);
        Assert.Equal(0, normal.FaultUntilTs);
        Assert.Equal(2, normal.Items.Count);
        Assert.All(normal.Items, item => Assert.Equal(TagQuality.Good, item.Quality));
        Assert.All(normal.Items, item => Assert.False(item.Forced));
        Assert.InRange(normal.Items[0].Value, 0, 100);
        Assert.InRange(normal.Items[1].Value, 0, 1);

        runtime.Force(device.Id, 1, 42);
        var forced = runtime.Snapshot(device, Options(), tags);
        Assert.True(forced.Items.Single(item => item.TagId == 1).Forced);
        Assert.Equal(42, forced.Items.Single(item => item.TagId == 1).Value);

        runtime.InjectFault(device.Id, SimulationFaultKind.BadQuality, 60);
        var bad = runtime.Snapshot(device, Options(), tags);
        Assert.Equal(SimulationFaultKind.BadQuality, bad.Fault);
        Assert.True(bad.FaultUntilTs > 0);
        Assert.All(bad.Items, item => Assert.Equal(TagQuality.Bad, item.Quality));
        Assert.All(bad.Items, item => Assert.Contains("坏质量", item.Error));
        Assert.Equal(42, bad.Items.Single(item => item.TagId == 1).Value);

        runtime.ClearFault(device.Id);
        Assert.Equal(SimulationFaultKind.None, runtime.Snapshot(device, Options(), tags).Fault);

        runtime.InjectFault(device.Id, SimulationFaultKind.CommFailure, 60);
        var comm = runtime.Snapshot(device, Options(), tags);
        Assert.All(comm.Items, item => Assert.Equal(TagQuality.CommFailure, item.Quality));
        Assert.All(comm.Items, item => Assert.Contains("通信中断", item.Error));
        Assert.Equal(42, comm.Items.Single(item => item.TagId == 1).Value);
        Assert.Equal(0, comm.Items.Single(item => item.TagId == 2).Value);

        runtime.ClearFault(device.Id);
        runtime.InjectFault(device.Id, SimulationFaultKind.Frozen, 60);
        var frozen = runtime.Snapshot(device, Options(), tags);
        var frozenAgain = runtime.Snapshot(device, Options(), tags);
        Assert.Equal(frozen.Items[0].Value, frozenAgain.Items[0].Value);
        Assert.Equal(frozen.Items[1].Value, frozenAgain.Items[1].Value);
    }

    [Fact]
    public void WriteRejectsNonFiniteAndFaultedDevices()
    {
        var runtime = new SimulationRuntime();
        var device = Device();

        Assert.Throws<InvalidOperationException>(() => runtime.Write(device.Id, 1, double.NaN));
        Assert.Throws<InvalidOperationException>(() => runtime.Write(device.Id, 1, double.PositiveInfinity));

        runtime.InjectFault(device.Id, SimulationFaultKind.CommFailure, 60);
        var comm = Assert.Throws<IOException>(() => runtime.Write(device.Id, 1, 5));
        Assert.Contains("通信中断", comm.Message);

        runtime.ClearFault(device.Id);
        runtime.InjectFault(device.Id, SimulationFaultKind.BadQuality, 60);
        var bad = Assert.Throws<IOException>(() => runtime.Write(device.Id, 1, 5));
        Assert.Contains("坏质量", bad.Message);
    }

    [Fact]
    public void ReleaseAllReturnsCountAndRemoveDropsState()
    {
        var runtime = new SimulationRuntime();
        var device = Device();
        var tags = new[] { Tag(1, TagDataType.Float, 0, 100), Tag(2, TagDataType.Float, 0, 100) };

        runtime.Force(device.Id, 1, 10);
        runtime.Force(device.Id, 2, 20);
        Assert.False(runtime.Release(device.Id, 99));
        Assert.Equal(2, runtime.ReleaseAll(device.Id));
        Assert.Equal(0, runtime.ReleaseAll(device.Id));

        runtime.Remove(device.Id);
        var regenerated = runtime.Read(device, Options(), tags);
        Assert.All(regenerated, value => Assert.Equal(TagQuality.Good, value.Quality));
        Assert.All(regenerated, value => Assert.InRange(value.Value, 0, 100));
    }

    [Fact]
    public void FaultExpiresAndReturnsToGoodQuality()
    {
        var runtime = new SimulationRuntime();
        var device = Device();
        var tags = new[] { Tag(1, TagDataType.Float, 0, 100) };

        runtime.InjectFault(device.Id, SimulationFaultKind.BadQuality, 1);
        Assert.Equal(TagQuality.Bad, runtime.Read(device, Options(), tags).Single().Quality);
        Thread.Sleep(1200);

        var recovered = runtime.Read(device, Options(), tags).Single();
        Assert.Equal(TagQuality.Good, recovered.Quality);

        var snapshot = runtime.Snapshot(device, Options(), tags);
        Assert.Equal(SimulationFaultKind.None, snapshot.Fault);
        Assert.Equal(0, snapshot.FaultUntilTs);
    }

    [Fact]
    public void EveryWaveformStaysWithinRangeAndRoundsIntegerTypes()
    {
        var runtime = new SimulationRuntime();
        var device = Device(3);
        var tags = new[]
        {
            Tag(1, TagDataType.Int16, -100, 100),
            Tag(2, TagDataType.UInt16, 0, 1000),
            Tag(3, TagDataType.Int32, -1000, 1000),
            Tag(4, TagDataType.UInt32, 0, 1000),
            Tag(5, TagDataType.Double, 0, 100),
            Tag(6, TagDataType.Bool, 0, 1)
        };

        foreach (var waveform in Enum.GetValues<SimulationWaveform>())
        {
            var options = Options(waveform, noise: waveform == SimulationWaveform.Sine ? 10 : 0);
            for (var run = 0; run < 3; run++)
            {
                var values = runtime.Read(device, options, tags);
                Assert.All(values, value => Assert.Equal(TagQuality.Good, value.Quality));
                Assert.All(values, value => Assert.InRange(value.Value, value.TagId == 1 || value.TagId == 3 ? -1000 : 0, 1000));
                Assert.Equal(Math.Round(values.Single(value => value.TagId == 1).Value), values.Single(value => value.TagId == 1).Value);
                Assert.Equal(Math.Round(values.Single(value => value.TagId == 3).Value), values.Single(value => value.TagId == 3).Value);
                Assert.InRange(values.Single(value => value.TagId == 6).Value, 0, 1);
                Thread.Sleep(60);
            }
        }
    }
}