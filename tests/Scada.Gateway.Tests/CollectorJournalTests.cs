using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.Realtime;

namespace Scada.Gateway.Tests;

public sealed class CollectorJournalTests
{
    private const string Revision = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task LongDiagnosticTextDoesNotPreventBadQualityMeasurementsFromBeingRecorded()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open(); var journal = new GatewayCollectorJournal(store, Revision);
        await journal.AppendAsync(1, new[] { new TagValue(1, 20, TagQuality.Bad, ScadaTime.NowMs(), Error: new string('x', 2000)) });
        var point = Assert.Single((await store.PeekAsync())!.Packet.Values);
        Assert.Equal(20, point.Value); Assert.Equal(TagQuality.Bad, point.Quality); Assert.Equal(1000, point.Error!.Length);
    }

    [Fact]
    public async Task CollectorCommitsTheWholeBatchBeforeUpdatingCacheOrPublishing()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open();
        var journal = new GateJournal(new GatewayCollectorJournal(store, Revision));
        await using var rig = new Rig(journal); var reader = rig.Bus.Subscribe();
        await rig.Engine.StartAsync(CancellationToken.None); await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(rig.Cache.Get(1)); Assert.False(reader.TryRead(out _)); Assert.Equal(0, (await store.StatusAsync()).PendingBatches);
        journal.Resume.TrySetResult(); await rig.UntilOnlineAsync();
        var packet = (await store.PeekAsync())!; var value = Assert.Single(packet.Packet.Values);
        Assert.Equal(10, value.Value); Assert.Equal(1234567, value.SourceTs); Assert.Equal(Revision, packet.Packet.ConfigurationRevision);
        Assert.Equal(value, rig.Cache.Get(1)); Assert.Equal(value, await reader.ReadAsync());
    }

    [Fact]
    public async Task AFullJournalFaultsTheWorkerWithoutPublishingAnUncommittedHealthySample()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open(1024);
        while (true)
        {
            try { await store.AppendAsync(1, Revision, new[] { new TagValue(1, 10, TagQuality.Good, ScadaTime.NowMs(), 1234567) }); }
            catch (GatewaySpoolFullException) { break; }
        }
        var before = await store.StatusAsync(); await using var rig = new Rig(new GatewayCollectorJournal(store, Revision)); var reader = rig.Bus.Subscribe();
        await rig.Engine.StartAsync(CancellationToken.None);
        await UntilAsync(() => rig.Engine.GetDiagnostics(1)?.State == "Faulted");
        Assert.Equal(before, await store.StatusAsync()); Assert.Equal(TagQuality.CommFailure, rig.Cache.Get(1)!.Quality);
        var published = new List<TagValue>(); while (reader.TryRead(out var value)) published.Add(value);
        Assert.DoesNotContain(published, x => x.Quality == TagQuality.Good);
        Assert.Contains("持久记录失败", rig.Engine.GetDiagnostics(1)!.CurrentError);
    }

    [Fact]
    public async Task FailureToPersistWriteFeedbackNeverRetriesThePhysicalWrite()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open();
        var journal = new RejectFeedbackJournal(new GatewayCollectorJournal(store, Revision));
        await using var rig = new Rig(journal); await rig.Engine.StartAsync(CancellationToken.None); await rig.UntilOnlineAsync();
        var result = await rig.Engine.WriteAsync(1, rig.Tag, 20);
        Assert.False(result.Success); Assert.Contains("写入已发送", result.Message); Assert.Contains("持久记录失败", result.Message);
        Assert.Equal(200, Assert.Single(rig.Driver.Writes)); Assert.Equal(10, rig.Cache.Get(1)!.Value);
        var batches = new List<GatewayTelemetry>();
        while (await store.PeekAsync() is { } pending)
        { batches.Add(pending.Packet); await store.AcknowledgeAsync(new(store.StreamId, pending.Sequence)); }
        Assert.NotEmpty(batches); Assert.DoesNotContain(batches.SelectMany(x => x.Values), x => x.Value == 20);
    }

    [Fact]
    public async Task CommunicationFailureIsJournaledAndCanBeReplayedAfterReopen()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory(); var store = temp.Open();
        await using var rig = new Rig(new GatewayCollectorJournal(store, Revision)); rig.Driver.FailConnect = true;
        await rig.Engine.StartAsync(CancellationToken.None);
        await UntilAsync(() => rig.Engine.GetStatuses().Any(x => !x.Online && x.CommFailures > 0));
        var value = Assert.Single((await temp.Open().PeekAsync())!.Packet.Values);
        Assert.Equal(TagQuality.CommFailure, value.Quality); Assert.Equal("simulated transport failure", value.Error);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)); while (!predicate()) await Task.Delay(10, timeout.Token); }

    private sealed class GateJournal(ICollectorJournal inner) : ICollectorJournal
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AppendAsync(int deviceId, IReadOnlyList<TagValue> values, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Resume.Task.WaitAsync(cancellationToken); await inner.AppendAsync(deviceId, values, cancellationToken); }
    }
    private sealed class RejectFeedbackJournal(ICollectorJournal inner) : ICollectorJournal
    {
        public Task AppendAsync(int deviceId, IReadOnlyList<TagValue> values, CancellationToken cancellationToken = default)
        { if (values.Any(x => x.Value == 20)) throw new IOException("simulated disk failure"); return inner.AppendAsync(deviceId, values, cancellationToken); }
    }
    private sealed class Rig : IAsyncDisposable
    {
        public Tag Tag { get; } = new() { Id = 1, DeviceId = 1, Name = "Scaled", Area = TagArea.HoldingRegister, DataType = TagDataType.UInt16, Scale = .1, Writable = true };
        public RealtimeCache Cache { get; } = new(); public RealtimeBus Bus { get; } = new(); public Driver Driver { get; } = new(); public CollectorEngine Engine { get; }
        public Rig(ICollectorJournal journal) => Engine = new CollectorEngine(new Snapshot(Tag), new Factory(Driver), new ReadPlanBuilder(), Cache, Bus,
            new Sink(), Options.Create(new ScadaSettings { CommandTimeoutMs = 2000 }), NullLogger<CollectorEngine>.Instance, journal);
        public Task UntilOnlineAsync() => UntilAsync(() => Engine.GetStatuses().Any(x => x.Online));
        public async ValueTask DisposeAsync() { await Engine.StopAsync(CancellationToken.None); Engine.Dispose(); Bus.Dispose(); }
    }
    private sealed class Driver : IDeviceDriver
    {
        public bool IsConnected { get; private set; } public string? LastError => null; public bool FailConnect;
        public ConcurrentQueue<double> Writes { get; } = new(); private double _raw = 100;
        public Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
        { if (FailConnect) throw new IOException("simulated transport failure"); IsConnected = true; return Task.CompletedTask; }
        public void Disconnect() => IsConnected = false;
        public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RawTagValue>>([new(1, _raw, TagQuality.Good, 1234567)]);
        public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) { Writes.Enqueue(value); _raw = value; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
    }
    private sealed class Factory(Driver driver) : IDeviceDriverFactory { public IDeviceDriver Create(Device device) => driver; }
    private sealed class Sink : IAlarmSink
    {
        public Task<int> RaiseDeviceOfflineAsync(Device device, string? message, long ts, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task RecoverDeviceOfflineAsync(int alarmEventId, long ts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Snapshot(Tag tag) : IConfigSnapshot
    {
        private readonly Device _device = new() { Id = 1, Name = "gateway-test", Enabled = true, PollIntervalMs = 10000, TimeoutMs = 1000 };
        public IReadOnlyList<Device> Devices => [_device]; public IReadOnlyList<Tag> Tags => [tag]; public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1; public long LoadedAt => 0; public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag value) { value = tag; return id == tag.Id; }
        public bool TryGetDevice(int id, out Device value) { value = _device; return id == _device.Id; }
    }
}
