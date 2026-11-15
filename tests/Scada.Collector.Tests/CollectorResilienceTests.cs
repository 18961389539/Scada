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

/// <summary>
/// 采集引擎的故障隔离与收尾：一台设备出问题不能拖垮其余设备，也不能把存储故障报成通信故障。
/// </summary>
public class CollectorResilienceTests
{
    [Fact]
    public async Task DeviceStartFailureIsIsolatedToThatDevice()
    {
        using var rig = new Rig(devices: [1, 2]);
        rig.Factory.FailFor = 2;
        await rig.Engine.StartAsync(CancellationToken.None);

        // 一台设备的协议/插件问题不影响另一台正常采集。
        await UntilAsync(() => rig.Engine.GetStatuses().Any(x => x.DeviceId == 1 && x.Online));
        Assert.True(rig.Factory.Created.Contains(1));
        Assert.False(rig.Factory.Created.Contains(2), "启动失败的设备不应留下半成品驱动");

        var failed = rig.Engine.GetDiagnostics(2);
        Assert.NotNull(failed);
        Assert.Equal("Faulted", failed!.State);
        Assert.Contains("启动失败", failed.CurrentError);
        Assert.True(rig.Engine.IsDeviceQuiescent(2), "启动失败不应留下占用设备的旧任务");
        await rig.Engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task JournalFailurePausesPublishingWithoutPretendingTheDeviceIsOffline()
    {
        using var rig = new Rig(devices: [1]);
        rig.Journal.Fail = true;
        await rig.Engine.StartAsync(CancellationToken.None);

        await UntilAsync(() => rig.Engine.GetDiagnostics(1)?.Events.Any(x => x.Kind == "StorageFaulted") == true, seconds: 8);
        var faulted = rig.Engine.GetDiagnostics(1)!;
        Assert.Equal("Faulted", faulted.State);
        Assert.Contains("持久记录失败", faulted.CurrentError);

        // 关键：链路是好的，不能报设备离线，也不能把测点扫成 CommFailure。
        Assert.Equal(0, rig.Alarm.Raised);
        Assert.NotEqual(TagQuality.CommFailure, rig.Cache.Get(1)?.Quality);
        Assert.False(rig.Engine.GetStatuses().Single().Online);

        // 日志恢复后自动接上：同一条采集连接继续发布。
        rig.Journal.Fail = false;
        await UntilAsync(() => rig.Engine.GetDiagnostics(1)?.State == "Online", seconds: 10);
        Assert.Equal(TagQuality.Good, rig.Cache.Get(1)!.Quality);
        Assert.Contains(rig.Engine.GetDiagnostics(1)!.Events, x => x.Kind == "StorageRecovered");
        Assert.Equal(0, rig.Alarm.Raised);
        await rig.Engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StuckReadIsAbandonedAfterGraceSoTheDeviceCanRecover()
    {
        using var rig = new Rig(devices: [1], settings: new ScadaSettings
        {
            CommandTimeoutMs = 500,
            CollectorWorkerStopGraceMs = 200,
            CollectorStuckIoGraceSeconds = 1
        });
        rig.Driver.StuckReads = true;
        await rig.Engine.StartAsync(CancellationToken.None);
        await UntilAsync(() => rig.Driver.Reads > 0);

        // 停采：读调用永不返回，worker 无法退出，先进入"旧采集 I/O 尚未退出"。
        rig.Config.Devices[0].Enabled = false;
        rig.Config.Version++;
        rig.Engine.RequestReload();
        await UntilAsync(() => !rig.Engine.IsDeviceQuiescent(1));
        Assert.Equal("Stopping", rig.Engine.GetDiagnostics(1)!.State);

        // 超过宽限后必须强制关链路并放弃旧任务，而不是让设备永久停采。
        await UntilAsync(() => rig.Engine.IsDeviceQuiescent(1), seconds: 8);
        Assert.True(rig.Driver.Disconnects > 0,
            $"放弃时必须主动断开驱动，让卡住的读返回（reads={rig.Driver.Reads}, disconnects={rig.Driver.Disconnects}, reads={rig.Driver.Reads}, state={rig.Engine.GetDiagnostics(1)?.State}）");
        await rig.Engine.StopAsync(CancellationToken.None);
    }

    private static async Task UntilAsync(Func<bool> condition, int seconds = 5)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Rig : IDisposable
    {
        private readonly RealtimeBus _bus = new();
        public Config Config { get; }
        public FakeFactory Factory { get; }
        public FakeDriver Driver { get; }
        public FakeJournal Journal { get; } = new();
        public CountingAlarmSink Alarm { get; } = new();
        public RealtimeCache Cache { get; } = new();
        public CollectorEngine Engine { get; }

        public Rig(int[] devices, ScadaSettings? settings = null)
        {
            Driver = new FakeDriver();
            Factory = new FakeFactory(Driver);
            Config = new Config(devices);
            Engine = new CollectorEngine(Config, Factory, new ReadPlanBuilder(), Cache, _bus, Alarm,
                Options.Create(settings ?? new ScadaSettings { CommandTimeoutMs = 500 }), NullLogger<CollectorEngine>.Instance, Journal);
        }

        public void Dispose()
        {
            Engine.Dispose();
            _bus.Dispose();
        }
    }

    private sealed class Config : IConfigSnapshot
    {
        public Config(int[] deviceIds)
        {
            Devices = deviceIds.Select(id => new Device { Id = id, Name = $"dev{id}", Enabled = true, PollIntervalMs = 100, TimeoutMs = 1000 }).ToArray();
            Tags = deviceIds.Select(id => new Tag { Id = id, DeviceId = id, Name = $"tag{id}", Area = TagArea.HoldingRegister, DataType = TagDataType.UInt16 }).ToArray();
        }

        public IReadOnlyList<Device> Devices { get; }
        public IReadOnlyList<Tag> Tags { get; }
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version { get; set; } = 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag tag) { tag = Tags.FirstOrDefault(x => x.Id == tagId)!; return tag is not null; }
        public bool TryGetDevice(int deviceId, out Device device) { device = Devices.FirstOrDefault(x => x.Id == deviceId)!; return device is not null; }
    }

    private sealed class FakeFactory(FakeDriver driver) : IDeviceDriverFactory
    {
        public int? FailFor;
        public ConcurrentBag<int> Created { get; } = [];

        public IDeviceDriver Create(Device device)
        {
            if (FailFor == device.Id) throw new InvalidOperationException("不支持设备协议");
            Created.Add(device.Id);
            return driver;
        }
    }

    private sealed class FakeDriver : IDeviceDriver
    {
        public volatile bool StuckReads;
        public volatile bool IsConnectedField;
        public int Reads;
        public int Disconnects { get; private set; }
        public bool IsConnected => IsConnectedField;
        public string? LastError => null;
        private readonly TaskCompletionSource _stuck = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default) { IsConnectedField = true; return Task.CompletedTask; }
        public void Disconnect() { Disconnects++; IsConnectedField = false; }
        public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            // 真实场景：串口/原生库卡在阻塞读上，取消令牌也唤不醒。
            if (StuckReads) await _stuck.Task;
            return plan.Tags.Select(x => new RawTagValue(x.Id, 10, TagQuality.Good, 1)).ToArray();
        }
        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeJournal : ICollectorJournal
    {
        public volatile bool Fail;
        public Task AppendAsync(int deviceId, IReadOnlyList<TagValue> values, CancellationToken cancellationToken = default) =>
            Fail ? Task.FromException(new IOException("模拟磁盘写满")) : Task.CompletedTask;
    }

    private sealed class CountingAlarmSink : IAlarmSink
    {
        public int Raised;
        public Task<int> RaiseDeviceOfflineAsync(Device device, string? message, long ts, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Raised);
            return Task.FromResult(1);
        }
        public Task RecoverDeviceOfflineAsync(int alarmEventId, long ts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
