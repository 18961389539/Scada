using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.Realtime;

namespace Scada.Collector.Tests;

public class CollectorEngineGuardTests
{
    private sealed class Rig : IDisposable
    {
        private readonly RealtimeBus _bus = new();
        public Config Config { get; } = new();
        public CollectorEngine Engine { get; }
        public Rig(INodeRole? role = null, ScadaSettings? settings = null)
        {
            Engine = new CollectorEngine(Config, new Factory(), new ReadPlanBuilder(), new RealtimeCache(), _bus,
                new Sink(), Options.Create(settings ?? new ScadaSettings()), NullLogger<CollectorEngine>.Instance, role: role);
        }
        public void Dispose() { Engine.Dispose(); _bus.Dispose(); }
    }

    [Fact]
    public async Task ReadRejectsStandbyDisabledReloadingAndUnknownDevices()
    {
        using (var rig = new Rig(role: new Role(false)))
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Engine.ReadAsync(1, [1]));

        using (var rig = new Rig(settings: new ScadaSettings { LocalCollectionEnabled = false }))
        {
            var disabled = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Engine.ReadAsync(1, [1]));
            Assert.Equal(LocalCollection.DisabledMessage, disabled.Message);
        }

        using (var rig = new Rig())
        {
            var reloading = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Engine.ReadAsync(1, [1]));
            Assert.Contains("重新加载", reloading.Message);
        }

        using (var rig = new Rig())
        {
            await rig.Engine.StartAsync(CancellationToken.None);
            await WaitForReconcileAsync(rig);
            var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Engine.ReadAsync(99, [1]));
            Assert.Contains("设备未启用或不可用", unknown.Message);
            await rig.Engine.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WriteAndTextWriteClassifyEveryPreconditionAsNotSent()
    {
        using (var rig = new Rig())
        {
            var foreign = new Tag { Id = 2, DeviceId = 2, Name = "foreign", Kind = TagKind.Physical, DataType = TagDataType.UInt16 };
            var notPhysical = new Tag { Id = 3, DeviceId = 1, Name = "calc", Kind = TagKind.Calculated, DataType = TagDataType.UInt16 };

            Assert.False((await rig.Engine.WriteAsync(1, foreign, 1)).Success);
            Assert.Contains("不是该设备的物理测点", (await rig.Engine.WriteAsync(1, foreign, 1)).Message);
            Assert.Contains("不是该设备的物理测点", (await rig.Engine.WriteAsync(1, notPhysical, 1)).Message);
            Assert.Contains("不是字符串类型", (await rig.Engine.WriteTextAsync(1, PhysicalTag(), "x")).Message);
        }

        using (var rig = new Rig(role: new Role(false)))
        {
            var result = await rig.Engine.WriteAsync(1, PhysicalTag(), 1);
            Assert.False(result.Success);
            Assert.Contains("备机", result.Message);
        }

        using (var rig = new Rig(settings: new ScadaSettings { LocalCollectionEnabled = false }))
        {
            var numeric = await rig.Engine.WriteAsync(1, PhysicalTag(), 1);
            Assert.Equal(LocalCollection.DisabledMessage, numeric.Message);
            var text = await rig.Engine.WriteTextAsync(1, StringTag(), "x");
            Assert.Equal(LocalCollection.DisabledMessage, text.Message);
        }

        using (var rig = new Rig())
        {
            var reloading = await rig.Engine.WriteAsync(1, PhysicalTag(), 1);
            Assert.Contains("重新加载", reloading.Message);
            var textReloading = await rig.Engine.WriteTextAsync(1, StringTag(), "x");
            Assert.Contains("重新加载", textReloading.Message);
        }

        using (var rig = new Rig())
        {
            await rig.Engine.StartAsync(CancellationToken.None);
            await WaitForReconcileAsync(rig);
            var unknown = await rig.Engine.WriteAsync(99, PhysicalTag(99), 1);
            Assert.Contains("设备不存在或未启用", unknown.Message);
            var textUnknown = await rig.Engine.WriteTextAsync(99, StringTag(99), "x");
            Assert.Contains("设备不存在或未启用", textUnknown.Message);
            await rig.Engine.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ProbeReturnsNullForUnknownDeviceAndRejectsInactiveNode()
    {
        using (var rig = new Rig(role: new Role(false)))
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Engine.ProbeAsync(1, PhysicalTag()));

        using (var rig = new Rig(settings: new ScadaSettings { LocalCollectionEnabled = false }))
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Engine.ProbeAsync(1, PhysicalTag()));

        using (var rig = new Rig())
            Assert.Null(await rig.Engine.ProbeAsync(99, PhysicalTag()));
    }

    [Fact]
    public void DiagnosticsAndChannelStatisticsExposeIdleAndEmptyStates()
    {
        using (var rig = new Rig(settings: new ScadaSettings { LocalCollectionEnabled = false }))
        {
            var diagnostics = rig.Engine.GetDiagnostics(1);
            Assert.NotNull(diagnostics);
            Assert.Equal("Disabled", diagnostics!.State);
            Assert.Equal(LocalCollection.DisabledMessage, diagnostics.CurrentError);
        }

        using (var rig = new Rig())
        {
            Assert.Null(rig.Engine.GetDiagnostics(99));
            var idle = rig.Engine.GetDiagnostics(1);
            Assert.NotNull(idle);
            Assert.True(idle!.PointCount > 0);

            var empty = rig.Engine.GetChannelStatistics(7);
            Assert.Equal(7, empty.ChannelId);
            Assert.Equal(0, empty.Connections);
            Assert.Empty(empty.Members);
        }
    }

    [Fact]
    public async Task StartRaisesThreadPoolMinimumWhenConfigured()
    {
        ThreadPool.GetMinThreads(out var originalWorkers, out var originalIo);
        var target = originalWorkers + 1;
        using var rig = new Rig(settings: new ScadaSettings { CollectorMinWorkerThreads = target });
        try
        {
            await rig.Engine.StartAsync(CancellationToken.None);
            ThreadPool.GetMinThreads(out var workers, out _);
            Assert.True(workers >= target, $"minimum worker threads {workers} < {target}");
            await rig.Engine.StopAsync(CancellationToken.None);
        }
        finally
        {
            ThreadPool.SetMinThreads(originalWorkers, originalIo);
        }
    }

    private static async Task WaitForReconcileAsync(Rig rig)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (rig.Engine.GetDiagnostics(1)?.ConfigurationPending != false) await Task.Delay(20, timeout.Token);
    }

    private static Tag PhysicalTag(int deviceId = 1) => new()
    {
        Id = 1, DeviceId = deviceId, Name = "tag1", Kind = TagKind.Physical,
        Area = TagArea.HoldingRegister, DataType = TagDataType.UInt16
    };

    private static Tag StringTag(int deviceId = 1) => new()
    {
        Id = 1, DeviceId = deviceId, Name = "tag1", Kind = TagKind.Physical,
        Area = TagArea.HoldingRegister, DataType = TagDataType.String
    };

    private sealed class Role(bool active) : INodeRole
    {
        public bool IsActive { get; } = active;
        public long Generation => 1;
    }

    private sealed class Sink : IAlarmSink
    {
        public Task<int> RaiseDeviceOfflineAsync(Device device, string? reason, long ts, CancellationToken cancellationToken = default)
            => Task.FromResult(1);
        public Task RecoverDeviceOfflineAsync(int alarmEventId, long ts, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class Factory : IDeviceDriverFactory
    {
        public IDeviceDriver Create(Device device) => new Driver();
    }

    private sealed class Driver : IDeviceDriver
    {
        public bool IsConnected => true;
        public string? LastError => null;
        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Disconnect() { }
        public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RawTagValue>>(plan.Tags.Select(tag => new RawTagValue(tag.Id, 10, TagQuality.Good, 1)).ToArray());
        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Config : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices { get; } =
        [
            new Device { Id = 1, Name = "dev1", Enabled = true, PollIntervalMs = 60_000, TimeoutMs = 1000 }
        ];
        public IReadOnlyList<Tag> Tags { get; } =
        [
            new Tag { Id = 1, DeviceId = 1, Name = "tag1", Kind = TagKind.Physical, Area = TagArea.HoldingRegister, DataType = TagDataType.UInt16 }
        ];
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version { get; set; } = 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag tag) { tag = Tags.FirstOrDefault(x => x.Id == tagId)!; return tag is not null; }
        public bool TryGetDevice(int deviceId, out Device device) { device = Devices.FirstOrDefault(x => x.Id == deviceId)!; return device is not null; }
    }
}