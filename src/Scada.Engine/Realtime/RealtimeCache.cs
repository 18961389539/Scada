using System.Collections.Concurrent;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Enums;

namespace Scada.Engine.Realtime;

public sealed class RealtimeCache(IConfigSnapshot? configuration = null) : IRealtimeCache
{
    private readonly ConcurrentDictionary<int, TagValue> _values = new();

    public void Set(TagValue value) => _values[value.TagId] = value;
    public void Remove(int tagId) => _values.TryRemove(tagId, out _);

    public TagValue? Get(int tagId)
    {
        if (!_values.TryGetValue(tagId, out var value)) return null;
        if (configuration?.TryGetTag(tagId, out var tag) == true && tag.Kind == TagKind.Calculated &&
            (!tag.CalculationEnabled || value.DefinitionRevision != tag.CalculationRevision || value.Ts <= configuration.LoadedAt))
            return value with { Quality = TagQuality.Uncertain, Error = "计算已停用或配置已变化，等待重新计算" };
        return value;
    }

    public IReadOnlyList<TagValue> GetMany(IEnumerable<int> tagIds) =>
        tagIds.Select(Get).Where(x => x is not null).Cast<TagValue>().ToList();

    public IReadOnlyDictionary<int, TagValue> Snapshot() =>
        _values.Keys.Select(Get).Where(x => x is not null).ToDictionary(x => x!.TagId, x => x!);
}
