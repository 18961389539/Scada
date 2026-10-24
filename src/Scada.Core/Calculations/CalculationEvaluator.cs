using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Core.Calculations;

public static class CalculationEvaluator
{
    public static TagValue Evaluate(Tag tag, TagExpression expression, IReadOnlyDictionary<int, Tag> tags,
        IReadOnlyDictionary<int, Device> devices, Func<int, TagValue?> read, long now, long loadedAt, TagValue? previous = null)
    {
        TagValue Failure(TagQuality quality, string error) => new(tag.Id, previous is { Value: var old } && double.IsFinite(old) ? old : 0, quality, now, null, error, tag.CalculationRevision);
        if (!tag.CalculationEnabled) return Failure(TagQuality.Uncertain, "计算测点已停用");
        var inputs = new Dictionary<int, TagValue>();
        foreach (var id in expression.Dependencies)
        {
            if (!tags.TryGetValue(id, out var source)) return Failure(TagQuality.Uncertain, $"输入 #{id} 不存在");
            if (source.Kind == TagKind.Physical && (source.DeviceId is not int deviceId || !devices.TryGetValue(deviceId, out var device) || !device.Enabled))
                return Failure(TagQuality.Uncertain, $"输入 #{id} 的设备不可用");
            var current = read(id);
            if (current is null || current.Quality != TagQuality.Good || !double.IsFinite(current.Value) ||
                source.Kind == TagKind.Calculated && (!source.CalculationEnabled || current.DefinitionRevision != source.CalculationRevision))
                return Failure(TagQuality.Uncertain, $"输入 #{id} 缺失、质量非正常或配置未确认");
            if (current.Ts <= loadedAt || current.Ts > now + 1000 || now - current.Ts > tag.InputMaxAgeMs)
                return Failure(TagQuality.Uncertain, $"输入 #{id} 已过期、早于配置或时间异常");
            inputs[id] = current;
        }
        try
        {
            var value = expression.Evaluate(id => inputs[id].Value);
            var sourceTs = inputs.Count == 0 ? (long?)null : inputs.Values.Min(x => x.SourceTs ?? x.Ts);
            return new(tag.Id, value, TagQuality.Good, now, sourceTs, null, tag.CalculationRevision);
        }
        catch (TagExpressionException ex) { return Failure(TagQuality.Bad, ex.Message); }
    }
}
