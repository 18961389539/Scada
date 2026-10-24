using System.Collections.Concurrent;
using System.Threading.Channels;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Engine.Realtime;

public sealed class RealtimeBus : IRealtimeBus, IDisposable
{
    private readonly ConcurrentDictionary<long, Channel<TagValue>> _subscribers = new();
    private long _nextId, _dropped;

    public int Backlog => _subscribers.Values.Sum(x => x.Reader.CanCount ? x.Reader.Count : 0);
    public long DroppedTotal => Interlocked.Read(ref _dropped);

    public void Publish(TagValue value)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            // 满即拒收并计数：宁可慢一拍被观测到，也不要悄悄挤掉已排队的数据。
            if (!subscriber.Writer.TryWrite(value))
            {
                Interlocked.Increment(ref _dropped);
            }
        }
    }

    public ChannelReader<TagValue> Subscribe()
    {
        var channel = Channel.CreateBounded<TagValue>(new BoundedChannelOptions(20_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _subscribers[Interlocked.Increment(ref _nextId)] = channel;
        return channel.Reader;
    }

    public void Dispose()
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryComplete();
        }

        _subscribers.Clear();
    }
}
