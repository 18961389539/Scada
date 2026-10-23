using System.Threading.Channels;
using Scada.Core.Models;

namespace Scada.Core.Interfaces;

public interface IRealtimeCache
{
    void Set(TagValue value);
    TagValue? Get(int tagId);
    IReadOnlyList<TagValue> GetMany(IEnumerable<int> tagIds);
    IReadOnlyDictionary<int, TagValue> Snapshot();
}

public interface IRealtimeBus
{
    int Backlog { get; }
    void Publish(TagValue value);
    ChannelReader<TagValue> Subscribe();
}
