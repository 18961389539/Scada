using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.Realtime;

namespace Scada.Collector.Tests;

public class ScanLaneTests
{
    [Fact]
    public void EffectiveFollowsDeviceWhenTagHasNoRate()
    {
        var device = new Device { Id = 1, PollIntervalMs = 1000 };
        Assert.Equal(1000, ScanCadence.Effective(device, new Tag { Id = 1 }));
    }

    [Fact]
    public void EffectivePrefersTagRate()
    {
        var device = new Device { Id = 1, PollIntervalMs = 1000 };
        Assert.Equal(100, ScanCadence.Effective(device, new Tag { Id = 1, ScanRateMs = 100 }));
    }

    [Fact]
    public void SlowestEffectiveUsesSlowestTag()
    {
        var device = new Device { Id = 1, PollIntervalMs = 500 };
        Tag[] tags = [new() { Id = 1, ScanRateMs = 100 }, new() { Id = 2, ScanRateMs = 5000 }, new() { Id = 3 }];
        Assert.Equal(5000, ScanCadence.SlowestEffective(device, tags));
    }

    [Fact]
    public void NonPhysicalTagCannotSetScanRate()
        => Assert.NotNull(ScanCadence.Validate(new Tag { Id = 1, Kind = TagKind.Calculated, ScanRateMs = 100 }));

    [Theory]
    [InlineData(9)]
    [InlineData(3_600_001)]
    public void ScanRateOutsideRangeIsRejected(int rate)
        => Assert.NotNull(ScanCadence.Validate(new Tag { Id = 1, Kind = TagKind.Physical, ScanRateMs = rate }));

    [Fact]
    public void FourTiersAreAllowedAndFiveAreRejected()
    {
        Assert.Null(ScanCadence.ValidateLaneBudget([100, 200, 300, 400], 400));
        Assert.NotNull(ScanCadence.ValidateLaneBudget([100, 200, 300, 400], 500));
        Assert.Null(ScanCadence.ValidateLaneBudget([null, 0, 100], null));
    }

    [Fact]
    public async Task FastTagIsPolledOnItsOwnScheduleWithoutDraggingTheSlowTag()
    {
        var fast = new Tag
        {
            Id = 1, DeviceId = 1, Name = "fast", Area = TagArea.HoldingRegister,
            DataType = TagDataType.UInt16, ScanRateMs = 100
        };
        var slow = new Tag
        {
            Id = 2, DeviceId = 1, Name = "slow", Area = TagArea.HoldingRegister, Address = 10,
            DataType = TagDataType.UInt16, ScanRateMs = 5000
        };
        var driver = new RecordingDriver();
        var config = new Snapshot([fast, slow]) { Device = { PollIntervalMs = 5000 } };
        using var bus = new RealtimeBus();
        var engine = new CollectorEngine(config, new Factory(driver), new ReadPlanBuilder(), new RealtimeCache(), bus,
            new AlarmSink(), Options.Create(new ScadaSettings()), NullLogger<CollectorEngine>.Instance);
        try
        {
            await engine.StartAsync(CancellationToken.None);
            await Task.Delay(1200);

            var fastReads = driver.Reads.Count(ids => ids.Contains(1));
            var slowReads = driver.Reads.Count(ids => ids.Contains(2));
            Assert.True(fastReads >= 5, $"快档应至少读取 5 次，实际 {fastReads} 次");
            Assert.InRange(slowReads, 1, 2);
            // 两个档位各自成批：快档读取不应把慢测点一起带上来。
            Assert.DoesNotContain(driver.Reads, ids => ids.Contains(1) && ids.Contains(2));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
            engine.Dispose();
        }
    }

    [Fact]
    public async Task TagsWithoutRateAreStillReadTogetherInOnePlan()
    {
        var a = new Tag { Id = 1, DeviceId = 1, Name = "a", Area = TagArea.HoldingRegister, DataType = TagDataType.UInt16 };
        var b = new Tag { Id = 2, DeviceId = 1, Name = "b", Area = TagArea.HoldingRegister, Address = 10, DataType = TagDataType.UInt16 };
        var driver = new RecordingDriver();
        var config = new Snapshot([a, b]) { Device = { PollIntervalMs = 100 } };
        using var bus = new RealtimeBus();
        var engine = new CollectorEngine(config, new Factory(driver), new ReadPlanBuilder(), new RealtimeCache(), bus,
            new AlarmSink(), Options.Create(new ScadaSettings()), NullLogger<CollectorEngine>.Instance);
        try
        {
            await engine.StartAsync(CancellationToken.None);
            await Task.Delay(400);
            Assert.NotEmpty(driver.Reads);
            Assert.All(driver.Reads, ids => Assert.Equal(2, ids.Length));
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
            engine.Dispose();
        }
    }

    private sealed class RecordingDriver : IDeviceDriver
    {
        public ConcurrentQueue<int[]> Reads { get; } = new();
        public bool IsConnected { get; private set; }
        public string? LastError => null;
        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }
        public void Disconnect() => IsConnected = false;
        public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
        {
            Reads.Enqueue(plan.Tags.Select(x => x.Id).ToArray());
            return Task.FromResult<IReadOnlyList<RawTagValue>>(plan.Tags.Select(x => new RawTagValue(x.Id, 1)).ToArray());
        }
        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Factory(RecordingDriver driver) : IDeviceDriverFactory
    {
        public IDeviceDriver Create(Device device) => driver;
    }

    private sealed class AlarmSink : IAlarmSink
    {
        public Task<int> RaiseDeviceOfflineAsync(Device device, string? reason, long ts, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task RecoverDeviceOfflineAsync(int alarmEventId, long ts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Snapshot(IEnumerable<Tag> tags) : IConfigSnapshot
    {
        public Device Device { get; set; } = new() { Id = 1, Name = "test", Enabled = true, PollIntervalMs = 1000, TimeoutMs = 1000 };
        public IReadOnlyList<Device> Devices => [Device];
        public IReadOnlyList<Tag> Tags { get; } = tags.ToList();
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag found)
        {
            found = Tags.FirstOrDefault(x => x.Id == id)!;
            return found is not null;
        }
        public bool TryGetDevice(int id, out Device found)
        {
            found = Device;
            return id == Device.Id;
        }
    }
}
