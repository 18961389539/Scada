using System.Collections.Concurrent;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Engine.Realtime;

public sealed class RealtimeCache : IRealtimeCache
{
    private readonly ConcurrentDictionary<int, TagValue> _values = new();

    public void Set(TagValue value) => _values[value.TagId] = value;

    public TagValue? Get(int tagId) => _values.TryGetValue(tagId, out var value) ? value : null;

    public IReadOnlyList<TagValue> GetMany(IEnumerable<int> tagIds) =>
        tagIds.Select(Get).Where(x => x is not null).Cast<TagValue>().ToList();

    public IReadOnlyDictionary<int, TagValue> Snapshot() =>
        new Dictionary<int, TagValue>(_values);
}
