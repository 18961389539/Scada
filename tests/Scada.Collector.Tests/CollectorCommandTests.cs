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
    public async Task BadQualityReadbackCannotReportSuccessfulControlAndPollingKeepsLastGoodValue()
    {
        await using var rig = new Rig(); await rig.StartAsync();
        rig.Driver.Quality = TagQuality.Bad; rig.Driver.RawValue = 9999;
        var result = await rig.Engine.WriteAsync(1, rig.Tag, 20);
        Assert.False(result.Success); Assert.Contains("结果未知", result.Message); Assert.Single(rig.Driver.Writes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (rig.Cache.Get(1)?.Quality != TagQuality.Bad) await Task.Delay(10, timeout.Token);
        Assert.Equal(10, rig.Cache.Get(1)!.Value);
    }
    [Fact]
    public async Task ScaledCommand_WritesInverseConversionAndConfirmsEngineeringReadback()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var result = await rig.Engine.WriteAsync(1, rig.Tag, 20);
        Assert.True(result.Success, result.Message);
        Assert.Equal(20, result.ReadBack);
        Assert.Equal(200, Assert.Single(rig.Driver.Writes));
        var snapshot = rig.Engine.GetDiagnostics(1)!;
        Assert.Equal(1, snapshot.Metrics.CommandsSucceeded);
        var point = Assert.Single(snapshot.Points); Assert.Equal(200, point.RawValue); Assert.Equal(20, point.Value);
        Assert.Equal(1234567, point.SourceTs); Assert.NotNull(point.LastGoodTs);
        var group = Assert.Single(snapshot.ReadGroups); Assert.Equal("寄存器", group.Unit); Assert.Equal(0, group.StartAddress);
    }

    [Fact]
    public async Task MissingReadResultBecomesBadAndRetainsLastValueInsteadOfRemainingHealthy()
    {
        await using var rig = new Rig(); await rig.StartAsync(); rig.Driver.OmitReads = true;
        await UntilAsync(() => rig.Engine.GetDiagnostics(1)?.State == "Degraded");
        var snapshot = rig.Engine.GetDiagnostics(1)!; var point = Assert.Single(snapshot.Points);
        Assert.Equal(TagQuality.Bad, point.Quality); Assert.Equal(10, point.Value); Assert.Null(point.RawValue);
        Assert.Contains("未返回", point.Error); Assert.NotNull(point.LastGoodTs);
        Assert.True(snapshot.Metrics.DegradedPolls > 0); Assert.Equal(0, snapshot.Metrics.PollFailures);
        Assert.Contains(snapshot.Events, x => x.Kind == "QualityChanged" && x.TagId == 1);
        Assert.Equal(TagQuality.Bad, rig.Cache.Get(1)!.Quality);
    }

    [Fact]
    public async Task TransportFailureShowsRetryAndManualReconnectRestoresAcquisition()
    {
        await using var rig = new Rig(); await rig.StartAsync(); rig.Driver.FailReads = true;
        await UntilAsync(() => rig.Engine.GetDiagnostics(1)?.State == "Offline");
        var offline = rig.Engine.GetDiagnostics(1)!;
        Assert.Equal(1, offline.Metrics.PollFailures); Assert.True(offline.Metrics.NextRetryTs > offline.CapturedTs);
        Assert.Equal("simulated read failure", offline.Metrics.LastFailure);
        await UntilAsync(() => rig.Cache.Get(1)?.Quality == TagQuality.CommFailure);
        rig.Driver.FailReads = false; Assert.True(rig.Engine.RequestReconnect(1));
        await UntilAsync(() => rig.Engine.GetDiagnostics(1) is { State: "Online", Metrics.ConnectAttempts: >= 2 });
        var recovered = rig.Engine.GetDiagnostics(1)!;
        Assert.Null(recovered.Metrics.NextRetryTs); Assert.Equal(0, recovered.Metrics.ConsecutiveFailures);
        Assert.NotNull(recovered.Metrics.LastFailureTs); Assert.Equal(TagQuality.Good, Assert.Single(recovered.Points).Quality);
        Assert.Contains(recovered.Events, x => x.Kind == "Reconnecting");
    }

    [Fact]
    public async Task ManualReconnectDoesNotInterruptAnExecutingWriteOrSendItTwice()
    {
        await using var rig = new Rig(); await rig.StartAsync(); rig.Driver.BlockWrites = true;
        var command = rig.Engine.WriteAsync(1, rig.Tag, 20);
        await rig.Driver.WriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var writing = rig.Engine.GetDiagnostics(1)!; Assert.Equal("Writing", writing.Operation); Assert.Equal(1, writing.OperationTagId);
        var disconnects = rig.Driver.Disconnects; Assert.True(rig.Engine.RequestReconnect(1));
        Assert.Equal(disconnects, rig.Driver.Disconnects); Assert.False(command.IsCompleted);
        rig.Driver.ResumeWrites.TrySetResult(); Assert.True((await command).Success);
        await UntilAsync(() => rig.Engine.GetDiagnostics(1) is { State: "Online", Metrics.ConnectAttempts: >= 2 });
        Assert.False(rig.Driver.DisconnectedDuringWrite); Assert.Single(rig.Driver.Writes);
    }

    [Fact]
    public async Task RemovingAllTagsClearsOldOnlineStatusAndDisabledDevicesHaveNoReconnectAction()
    {
        await using var rig = new Rig(); await rig.StartAsync();
        rig.Config.Tags = []; rig.Config.Version++;
        await UntilAsync(() => rig.Engine.GetStatuses().Count == 0);
        Assert.Equal("NoTags", rig.Engine.GetDiagnostics(1)!.State); Assert.False(rig.Engine.RequestReconnect(1));
        rig.Config.Device.Enabled = false; rig.Config.Version++;
        Assert.Equal("Disabled", rig.Engine.GetDiagnostics(1)!.State);
        Assert.Null(rig.Engine.GetDiagnostics(999)); Assert.False(rig.Engine.RequestReconnect(999));
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task GatewayAssignedDevicesNeverCreateALocalAcquisitionWorker()
    {
        await using var rig = new Rig(); rig.Config.Device.GatewayId = 42; rig.Driver.FailConnections = true;
        await rig.Engine.StartAsync(CancellationToken.None);
        Assert.Empty(rig.Engine.GetStatuses()); Assert.True(rig.Engine.IsDeviceQuiescent(1)); Assert.False(rig.Driver.IsConnected);
    }

    [Fact]
    public async Task DisablingDeviceDoesNotReportQuiescenceUntilItsActiveReadActuallyExits()
    {
        await using var rig = new Rig(); await rig.StartAsync(); rig.Driver.BlockReads = true;
        await rig.Driver.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(rig.Engine.IsDeviceQuiescent(1));
        rig.Config.Device.Enabled = false; rig.Config.Version++;
        await UntilAsync(() => rig.Engine.IsDeviceQuiescent(1) && rig.Engine.GetStatuses().Count == 0);
        Assert.Empty(rig.Engine.GetStatuses());
    }

    [Fact]
    public async Task ConnectingFailureHasItsOwnCounterAndDoesNotCountAsAReadAttempt()
    {
        await using var rig = new Rig(); rig.Driver.FailConnections = true;
        await rig.Engine.StartAsync(CancellationToken.None);
        await UntilAsync(() => rig.Engine.GetDiagnostics(1)?.State == "Offline");
        var snapshot = rig.Engine.GetDiagnostics(1)!;
        Assert.Equal(1, snapshot.Metrics.ConnectAttempts); Assert.Equal(1, snapshot.Metrics.ConnectFailures);
        Assert.Equal(0, snapshot.Metrics.PollAttempts); Assert.Null(snapshot.Metrics.LastSuccessTs);
        Assert.Equal("simulated connect failure", snapshot.Metrics.LastFailure); Assert.NotNull(snapshot.Metrics.NextRetryTs);
    }

    [Fact]
    public async Task ABlockedReadShowsStaleLastSampleEvenWhenItsOriginalQualityWasGood()
    {
        await using var rig = new Rig(); await rig.StartAsync(); rig.Driver.BlockReads = true;
        await rig.Driver.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await UntilAsync(() => rig.Engine.GetDiagnostics(1)!.Quality.Stale == 1);
        var snapshot = rig.Engine.GetDiagnostics(1)!; var point = Assert.Single(snapshot.Points);
        Assert.Equal("Reading", snapshot.Operation); Assert.Equal(TagQuality.Good, point.Quality); Assert.True(point.Stale);
        Assert.True(point.AgeMs > snapshot.StaleAfterMs); Assert.Equal(10, point.Value);
    }

    [Fact]
    public async Task PendingConfigurationRejectsWritesUntilTheWorkerIsRebuilt()
    {
        await using var rig = new Rig(); await rig.StartAsync(); rig.Config.Version++;
        Assert.True(rig.Engine.GetDiagnostics(1)!.ConfigurationPending);
        var result = await rig.Engine.WriteAsync(1, rig.Tag, 20);
        Assert.False(result.Success); Assert.Contains("重新加载", result.Message); Assert.Empty(rig.Driver.Writes);
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
        Assert.Equal(1, rig.Engine.GetDiagnostics(1)!.PendingCommands);
        rig.Driver.BlockReads = false; rig.Driver.ResumeReads.TrySetResult();
        await rig.Driver.Resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The worker processes queued commands after this read, then reaches the following polling cycle.
        await rig.Driver.NextPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(rig.Driver.Writes);
        Assert.Equal(1, rig.Engine.GetDiagnostics(1)!.Metrics.CommandsExpired);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Tag Tag { get; } = new() { Id = 1, DeviceId = 1, Name = "Scaled", Area = TagArea.HoldingRegister, DataType = TagDataType.UInt16, Scale = .1, Writable = true };
        public FakeDriver Driver { get; } = new();
        public CollectorEngine Engine { get; }
        public Snapshot Config { get; }
        public RealtimeCache Cache { get; } = new();
        private readonly RealtimeBus _bus = new();
        public Rig(int timeout = 2000)
        {
            Config = new Snapshot(Tag);
            Engine = new CollectorEngine(Config, new Factory(Driver), new ReadPlanBuilder(), Cache, _bus,
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
        public TagQuality Quality = TagQuality.Good;
        public bool IgnoreWrites;
        public volatile bool BlockReads;
        public volatile bool OmitReads, FailReads, BlockWrites, FailConnections;
        public int Disconnects; public bool DisconnectedDuringWrite;
        private volatile bool _writing;
        public TaskCompletionSource WriteBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeWrites { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeReads { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NextPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _resumed;
        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default) { if (FailConnections) throw new IOException("simulated connect failure"); IsConnected = true; return Task.CompletedTask; }
        public void Disconnect() { if (_writing) DisconnectedDuringWrite = true; Interlocked.Increment(ref Disconnects); IsConnected = false; }
        public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
        {
            if (BlockReads) { ReadBlocked.TrySetResult(); await ResumeReads.Task.WaitAsync(cancellationToken); _resumed = true; Resumed.TrySetResult(); }
            else if (_resumed) NextPoll.TrySetResult();
            if (FailReads) throw new IOException("simulated read failure");
            if (OmitReads) return [];
            return [new RawTagValue(1, RawValue, Quality, 1234567, Quality == TagQuality.Good ? null : "BadSensorFailure")];
        }
        public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
        {
            _writing = true;
            try { if (BlockWrites) { WriteBlocked.TrySetResult(); await ResumeWrites.Task.WaitAsync(cancellationToken); } Writes.Enqueue(value); if (!IgnoreWrites) RawValue = value; }
            finally { _writing = false; }
        }
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
        public Device Device { get; } = new() { Id = 1, Name = "test", Enabled = true, PollIntervalMs = 100, TimeoutMs = 1000 };
        public IReadOnlyList<Device> Devices => [Device]; public IReadOnlyList<Tag> Tags { get; set; } = [tag]; public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version { get; set; } = 1; public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag found) { found = tag; return id == tag.Id; }
        public bool TryGetDevice(int id, out Device found) { found = Device; return id == Device.Id; }
    }
}
