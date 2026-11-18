using System.Collections.Concurrent;
using System.Threading.Channels;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Engine.Realtime;

public sealed class RealtimeBus : IRealtimeBus, IDisposable
{
    public const int SubscriberCapacity = 20_000;
    public const int HistoryCapacity = 100_000;
    public const int HistoryGapCapacity = 4_096;

    // 历史队列满时的等待上限。默认 0（非阻塞）：发布方是采集与计算线程，
    // 为历史落盘让步会把磁盘抖动直接传导成采集延迟；历史链路本身有 HistorySpool 落盘兜底，
    // 丢掉一条实时点并计入 HistoryRejected，比拖慢采集更可接受。
    // 需要"宁可延迟也不丢点"的部署可把 ScadaSettings.RealtimeBusHistoryWaitMs 配成正数。
    private readonly TimeSpan _historyWait;

    private readonly ConcurrentDictionary<long, Channel<TagValue>> _subscribers = new();
    private readonly ConcurrentDictionary<long, Channel<TagValue>> _history = new();
    private readonly ConcurrentDictionary<long, Channel<TagValue>> _historyGaps = new();
    private long _nextId, _dropped, _historyRejected, _historyGapRejected;

    public RealtimeBus(TimeSpan? historyBackpressureWait = null)
        => _historyWait = historyBackpressureWait is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero;

public int Backlog => Count(_subscribers) + Count(_history) + Count(_historyGaps);
    public long DroppedTotal => Interlocked.Read(ref _dropped);
    public long HistoryRejected => Interlocked.Read(ref _historyRejected);
    public long HistoryGapRejected => Interlocked.Read(ref _historyGapRejected);

    public void Publish(TagValue value)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            // 满即拒收并计数：宁可慢一拍被观测到，也不要悄悄挤掉已排队的数据。
            if (!subscriber.Writer.TryWrite(value)) Interlocked.Increment(ref _dropped);
        }
        foreach (var subscriber in _history.Values)
        {
            if (subscriber.Writer.TryWrite(value)) continue;
            // 仅在显式配置等待上限时才阻塞发布线程；默认直接拒收并计数，避免把磁盘抖动传导给采集。
            if (_historyWait > TimeSpan.Zero && subscriber.Writer.WaitToWriteAsync().AsTask().Wait(_historyWait) && subscriber.Writer.TryWrite(value)) continue;
            Interlocked.Increment(ref _historyRejected);
            var gap = value with { Ts = ScadaTime.NowMs(), Quality = TagQuality.Uncertain, Error = "历史写入队列已满，数据点已丢弃" };
            foreach (var gaps in _historyGaps.Values)
                if (!gaps.Writer.TryWrite(gap)) Interlocked.Increment(ref _historyGapRejected);
        }
    }

    public ChannelReader<TagValue> Subscribe() => Create(_subscribers, SubscriberCapacity, out _).Reader;

    public ChannelReader<TagValue> SubscribeHistory() => Create(_history, HistoryCapacity, out _).Reader;
    public ChannelReader<TagValue> SubscribeHistoryGaps() => Create(_historyGaps, HistoryGapCapacity, out _).Reader;

    public IDisposable Subscribe(out ChannelReader<TagValue> reader)
    {
        var channel = Create(_subscribers, SubscriberCapacity, out var id);
        reader = channel.Reader;
        return new Lease(_subscribers, id, channel.Writer);
    }

    public IDisposable SubscribeHistory(out ChannelReader<TagValue> reader)
    {
        var channel = Create(_history, HistoryCapacity, out var id);
        reader = channel.Reader;
        return new Lease(_history, id, channel.Writer);
    }

    public IDisposable SubscribeHistoryGaps(out ChannelReader<TagValue> reader)
    {
        var channel = Create(_historyGaps, HistoryGapCapacity, out var id);
        reader = channel.Reader;
        return new Lease(_historyGaps, id, channel.Writer);
    }

    public void Dispose()
    {
        foreach (var subscriber in _subscribers.Values.Concat(_history.Values).Concat(_historyGaps.Values))
            subscriber.Writer.TryComplete();
        _subscribers.Clear();
        _history.Clear();
        _historyGaps.Clear();
    }

    private Channel<TagValue> Create(ConcurrentDictionary<long, Channel<TagValue>> target, int capacity, out long id)
    {
        var channel = Channel.CreateBounded<TagValue>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        id = Interlocked.Increment(ref _nextId);
        target[id] = channel;
        return channel;
    }

    // 订阅租约：释放时把队列从总线摘掉，并完成写入端让仍在读的消费者看到 EOF。
    private sealed class Lease(ConcurrentDictionary<long, Channel<TagValue>> target, long id, ChannelWriter<TagValue> writer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            target.TryRemove(id, out _);
            writer.TryComplete();
        }
    }

    private static int Count(ConcurrentDictionary<long, Channel<TagValue>> target) =>
        target.Values.Sum(x => x.Reader.CanCount ? x.Reader.Count : 0);
}
