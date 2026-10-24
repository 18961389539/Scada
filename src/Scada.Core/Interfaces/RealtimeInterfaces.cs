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
    void Publish(TagValue value);
    ChannelReader<TagValue> Subscribe();
}
