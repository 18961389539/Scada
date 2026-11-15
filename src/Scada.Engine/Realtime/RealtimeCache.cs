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
        // 一次 TryGetTag 覆盖两种降级判断。原来是两次独立查找，而 Get 在推送/报警/脚本采样里都是热路径。
        if (configuration?.TryGetTag(tagId, out var tag) != true) return value;
        if (tag.Kind == TagKind.Calculated &&
            (!tag.CalculationEnabled || value.DefinitionRevision != tag.CalculationRevision || value.Ts <= configuration.LoadedAt))
            return value with { Quality = TagQuality.Uncertain, Error = "计算已停用或配置已变化，等待重新计算" };
        if (tag.Kind != TagKind.Calculated && value.DefinitionRevision is long revision && revision != tag.CalculationRevision)
            return value with { Quality = TagQuality.Uncertain, Error = "量程已变化，等待新采样" };
        return value;
    }

    public IReadOnlyList<TagValue> GetMany(IEnumerable<int> tagIds)
    {
        var result = new List<TagValue>(tagIds.TryGetNonEnumeratedCount(out var count) ? count : 0);
        foreach (var tagId in tagIds)
            if (Get(tagId) is { } value) result.Add(value);
        return result;
    }

    public IReadOnlyDictionary<int, TagValue> Snapshot()
    {
        // 直接遍历一次 _values：原写法对每个 key 调一次 Get，还要串起 Select/Where/ToDictionary 三个迭代器。
        var result = new Dictionary<int, TagValue>(_values.Count);
        foreach (var pair in _values)
            if (Get(pair.Key) is { } value) result[value.TagId] = value;
        return result;
    }
}
