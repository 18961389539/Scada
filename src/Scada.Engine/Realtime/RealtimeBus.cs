using System.Collections.Concurrent;
using System.Threading.Channels;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Engine.Realtime;

public sealed class RealtimeBus : IRealtimeBus, IDisposable
{
    public const int SubscriberCapacity = 20_000;
    public const int HistoryCapacity = 100_000;
    public static readonly TimeSpan HistoryWait = TimeSpan.FromMilliseconds(25);

    private readonly ConcurrentDictionary<long, Channel<TagValue>> _subscribers = new();
    private readonly ConcurrentDictionary<long, Channel<TagValue>> _history = new();
    private long _nextId, _dropped, _historyRejected;

    public int Backlog => Count(_subscribers) + Count(_history);
    public long DroppedTotal => Interlocked.Read(ref _dropped);
    public long HistoryRejected => Interlocked.Read(ref _historyRejected);

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
            // 历史队列满时短暂让采集侧等写入方取走一条，仍满才拒收新点，已排队的点不被挤掉。
            if (subscriber.Writer.WaitToWriteAsync().AsTask().Wait(HistoryWait) && subscriber.Writer.TryWrite(value)) continue;
            Interlocked.Increment(ref _historyRejected);
        }
    }

    public ChannelReader<TagValue> Subscribe() => Subscribe(_subscribers, SubscriberCapacity);

    public ChannelReader<TagValue> SubscribeHistory() => Subscribe(_history, HistoryCapacity);

    public void Dispose()
    {
        foreach (var subscriber in _subscribers.Values.Concat(_history.Values))
            subscriber.Writer.TryComplete();
        _subscribers.Clear();
        _history.Clear();
    }

    private ChannelReader<TagValue> Subscribe(ConcurrentDictionary<long, Channel<TagValue>> target, int capacity)
    {
        var channel = Channel.CreateBounded<TagValue>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        target[Interlocked.Increment(ref _nextId)] = channel;
        return channel.Reader;
    }

    private static int Count(ConcurrentDictionary<long, Channel<TagValue>> target) =>
        target.Values.Sum(x => x.Reader.CanCount ? x.Reader.Count : 0);
}
