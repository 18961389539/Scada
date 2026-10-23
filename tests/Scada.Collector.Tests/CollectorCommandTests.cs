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

public class CollectorCommandTests
{
    [Fact]
    public async Task ScaledCommand_WritesInverseConversionAndConfirmsEngineeringReadback()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var result = await rig.Engine.WriteAsync(1, rig.Tag, 20);
        Assert.True(result.Success, result.Message);
        Assert.Equal(20, result.ReadBack);
        Assert.Equal(200, Assert.Single(rig.Driver.Writes));
    }

    [Fact]
    public async Task MismatchedReadback_IsFailureAndNeverResendsWrite()
    {
        await using var rig = new Rig(); rig.Driver.IgnoreWrites = true;
        await rig.StartAsync();
        var result = await rig.Engine.WriteAsync(1, rig.Tag, 20);
        Assert.False(result.Success);
        Assert.Contains("回读不匹配", result.Message);
        Assert.Single(rig.Driver.Writes);
    }

    [Fact]
    public async Task CommandThatExpiresInQueue_IsNotWrittenLater()
    {
        await using var rig = new Rig(timeout: 100);
        await rig.StartAsync();
        rig.Driver.BlockReads = true;
        await rig.Driver.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await rig.Engine.WriteAsync(1, rig.Tag, 20);
        Assert.False(result.Success); Assert.Contains("未下发", result.Message);
        rig.Driver.BlockReads = false; rig.Driver.ResumeReads.TrySetResult();
        await rig.Driver.Resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The worker processes queued commands after this read, then reaches the following polling cycle.
        await rig.Driver.NextPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(rig.Driver.Writes);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Tag Tag { get; } = new() { Id = 1, DeviceId = 1, Name = "Scaled", Area = TagArea.HoldingRegister, DataType = TagDataType.UInt16, Scale = .1, Writable = true };
        public FakeDriver Driver { get; } = new();
        public CollectorEngine Engine { get; }
        private readonly RealtimeBus _bus = new();
        public Rig(int timeout = 2000)
        {
            Engine = new CollectorEngine(new Snapshot(Tag), new Factory(Driver), new ReadPlanBuilder(), new RealtimeCache(), _bus,
                new AlarmSink(), Options.Create(new ScadaSettings { CommandTimeoutMs = timeout }), NullLogger<CollectorEngine>.Instance);
        }
        public async Task StartAsync()
        {
            await Engine.StartAsync(CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Engine.GetStatuses().Any(x => x.Online)) await Task.Delay(10, timeout.Token);
        }
        public async ValueTask DisposeAsync() { await Engine.StopAsync(CancellationToken.None); Engine.Dispose(); _bus.Dispose(); }
    }
    private sealed class FakeDriver : IDeviceDriver
    {
        public bool IsConnected { get; private set; }
        public string? LastError => null;
        public ConcurrentQueue<double> Writes { get; } = new();
        public double RawValue = 100;
        public bool IgnoreWrites;
        public volatile bool BlockReads;
        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeReads { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NextPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _resumed;
        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default) { IsConnected = true; return Task.CompletedTask; }
        public void Disconnect() => IsConnected = false;
        public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
        {
            if (BlockReads) { ReadBlocked.TrySetResult(); await ResumeReads.Task.WaitAsync(cancellationToken); _resumed = true; Resumed.TrySetResult(); }
            else if (_resumed) NextPoll.TrySetResult();
            return [new RawTagValue(1, RawValue)];
        }
        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
        { Writes.Enqueue(value); if (!IgnoreWrites) RawValue = value; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
    }
    private sealed class Factory(FakeDriver driver) : IDeviceDriverFactory { public IDeviceDriver Create(Device device) => driver; }
    private sealed class AlarmSink : IAlarmSink
    {
        public Task<int> RaiseDeviceOfflineAsync(Device device, string message, long ts, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task RecoverDeviceOfflineAsync(int alarmEventId, long ts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Snapshot(Tag tag) : IConfigSnapshot
    {
        private readonly Device _device = new() { Id = 1, Name = "test", Enabled = true, PollIntervalMs = 100, TimeoutMs = 1000 };
        public IReadOnlyList<Device> Devices => [_device]; public IReadOnlyList<Tag> Tags => [tag]; public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1; public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag found) { found = tag; return id == tag.Id; }
        public bool TryGetDevice(int id, out Device found) { found = _device; return id == _device.Id; }
    }
}
