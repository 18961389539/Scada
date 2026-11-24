using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.History;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public sealed class HistoryWriterBusDropTests
{
    [Fact]
    public void ReportBusDropsRecordsHistoryRejectedUsingGapMarkTimestampsAndIgnoresUiDrops()
    {
        using var dir = new TempDir();
        var bus = new StubBus { HistoryRejected = 3, DroppedTotal = 40 };
        var log = new HistoryLossLog();
        using var writer = Writer(bus, dir.Path, log);

        writer.NoteGapMark(1_700_000_000_000);
        writer.NoteGapMark(1_700_000_000_250);
        writer.ReportBusDrops();

        var gap = Assert.Single(log.Snapshot());
        Assert.Equal(HistoryGapKind.Rejected, gap.Kind);
        Assert.Equal(1_700_000_000_000, gap.From);
        Assert.Equal(1_700_000_000_250, gap.To);
        Assert.Contains("总线拒收 3 条历史样本", gap.Detail);

        bus.DroppedTotal = 99;
        writer.ReportBusDrops();
        Assert.Single(log.Snapshot());
        Assert.DoesNotContain(log.Snapshot(), x => x.Detail is not null && x.Detail.Contains("丢弃"));
    }

    [Fact]
    public void ReportBusDropsRecordsHistoryGapRejectedOverflow()
    {
        using var dir = new TempDir();
        var bus = new StubBus { HistoryGapRejected = 2 };
        var log = new HistoryLossLog();
        using var writer = Writer(bus, dir.Path, log);

        var before = ScadaTime.NowMs();
        writer.ReportBusDrops();
        var after = ScadaTime.NowMs();

        var gap = Assert.Single(log.Snapshot());
        Assert.Equal(HistoryGapKind.Rejected, gap.Kind);
        Assert.InRange(gap.From, before - 1_000, after);
        Assert.InRange(gap.To, before, after + 1_000);
        Assert.Contains("缺口标记队列溢出 2 条", gap.Detail);
    }

    private static HistoryWriterService Writer(IRealtimeBus bus, string root, HistoryLossLog log) =>
        new(bus, new RealtimeCache(), new EmptySnapshot(), new NoopStore(),
            Options.Create(new ScadaSettings { ArchiveFlushIntervalMs = 200, HistoryRoot = root }),
            new HistorySpool(root), NullLogger<HistoryWriterService>.Instance, lossLog: log);

    private sealed class StubBus : IRealtimeBus
    {
        public int Backlog => 0;
        public long DroppedTotal { get; set; }
        public long HistoryRejected { get; set; }
        public long HistoryGapRejected { get; set; }
        public void Publish(TagValue value) { }
        public System.Threading.Channels.ChannelReader<TagValue> Subscribe() => System.Threading.Channels.Channel.CreateUnbounded<TagValue>().Reader;
    }

    private sealed class EmptySnapshot : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => [];
        public IReadOnlyList<Tag> Tags => [];
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag tag) { tag = null!; return false; }
        public bool TryGetDevice(int deviceId, out Device device) { device = null!; return false; }
    }

    private sealed class NoopStore : IHistoryStore
    {
        public Task WriteRawAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<HistorySeries>> QueryRawAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistorySeries>>([]);
        public Task WriteAggregate5MinAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WriteAggregateHourAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<HistorySeries>> QueryAggregate5MinAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistorySeries>>([]);
        public Task<IReadOnlyList<HistorySeries>> QueryAggregateHourAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistorySeries>>([]);
        public Task<IReadOnlyList<AggregateValue>> QueryRawAggregatesAsync(long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AggregateValue>>([]);
        public Task<IReadOnlyList<AggregateValue>> Query5MinAggregatesAsync(long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AggregateValue>>([]);
        public Task<IReadOnlyList<AggregatePoint>> QueryHourAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AggregatePoint>>([]);
        public Task<IReadOnlyList<AggregatePoint>> QueryAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AggregatePoint>>([]);
    }
}
