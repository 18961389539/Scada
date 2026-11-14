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
    void Publish(TagValue value);
    ChannelReader<TagValue> Subscribe();
    // 历史写入使用更大的队列，满了先等一拍再拒收，不和界面订阅互相挤掉。
    ChannelReader<TagValue> SubscribeHistory() => Subscribe();
}
