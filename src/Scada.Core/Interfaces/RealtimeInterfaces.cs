using System.Threading.Channels;
using Scada.Core.Models;

namespace Scada.Core.Interfaces;

public interface IRealtimeCache
{
    void Set(TagValue value);
    void Remove(int tagId) { }
    TagValue? Get(int tagId);
    IReadOnlyList<TagValue> GetMany(IEnumerable<int> tagIds);
    IReadOnlyDictionary<int, TagValue> Snapshot();
}

public interface IRealtimeBus
{
    int Backlog { get; }
    // 订阅端积压到上限后被拒收的采集值累计数；用于让"丢数"可观测，而不是静默发生。
    long DroppedTotal { get; }
    // 历史订阅在短暂反压后仍写不进去的条数。普通订阅的丢弃不计入这里。
    long HistoryRejected => 0;
    long HistoryGapRejected => 0;
    void Publish(TagValue value);
    ChannelReader<TagValue> Subscribe();
    // 历史写入使用更大的队列，满了先等一拍再拒收，不和界面订阅互相挤掉。
    ChannelReader<TagValue> SubscribeHistory() => Subscribe();
    // 历史队列满时产生的缺口标记走独立小队列，避免采集线程被落盘阻塞。
    ChannelReader<TagValue> SubscribeHistoryGaps() => Channel.CreateUnbounded<TagValue>().Reader;

    // 带租约的订阅：Dispose 后从总线移除。短生命周期消费者（按需聚合器、测试替身）必须用这个，
    // 否则每次订阅都会永久留下一个没人读的队列——缓冲满后每次发布都多一次失败写入，
    // 还会把 DroppedTotal 一起污染，掩盖真正的丢数。
    IDisposable Subscribe(out ChannelReader<TagValue> reader)
    {
        reader = Subscribe();
        return EmptySubscription.Instance;
    }

    IDisposable SubscribeHistory(out ChannelReader<TagValue> reader)
    {
        reader = SubscribeHistory();
        return EmptySubscription.Instance;
    }

    IDisposable SubscribeHistoryGaps(out ChannelReader<TagValue> reader)
    {
        reader = SubscribeHistoryGaps();
        return EmptySubscription.Instance;
    }
}

// 接口默认实现用的空句柄：替身只实现无租约订阅时，租约退化为"不可退订"。
internal sealed class EmptySubscription : IDisposable
{
    public static readonly EmptySubscription Instance = new();
    public void Dispose() { }
}
