using System.Reflection;
using Scada.Core.Entities;

namespace Scada.Core.Tests;

/// <summary>
/// ApplyEditableFrom 是三张手写字段清单（Tag 20 项 / Device 27 项 / AlarmRule 23 项）。
/// 漏抄一个字段不会编译失败、不会告警，只会在保存后表现为"改了没生效"。
/// 这组用例用反射守住：新增字段若既不复制、也没进排除清单，立刻变红。
/// </summary>
public class EditableFieldCopyTests
{
    private static readonly HashSet<string> TagNotEditable = new(StringComparer.Ordinal)
    {
        // 归属与身份由设备/计算任务维护；计算相关字段走各自端点。
        nameof(Tag.Id), nameof(Tag.Kind), nameof(Tag.AreaId), nameof(Tag.Expression), nameof(Tag.CalculationEnabled),
        nameof(Tag.CalculationIntervalMs), nameof(Tag.InputMaxAgeMs), nameof(Tag.CalculationRevision),
        nameof(Tag.JobId), nameof(Tag.JobOutput)
    };

    private static readonly HashSet<string> DeviceNotEditable = new(StringComparer.Ordinal)
    {
        // 归属由网关侧维护；DisabledSinceTs 由调用方按"本次是否由启用改为停用"决定。
        nameof(Device.Id), nameof(Device.GatewayId), nameof(Device.DisabledSinceTs)
    };

    private static readonly HashSet<string> AlarmRuleNotEditable = new(StringComparer.Ordinal)
    {
        // Key 由服务端生成并保持稳定；停用（out of service）有独立的业务端点与审本文案。
        nameof(AlarmRule.Id), nameof(AlarmRule.Key), nameof(AlarmRule.OutOfService), nameof(AlarmRule.OutOfServiceReason)
    };

    [Fact]
    public void TagCopiesEveryEditableField() =>
        AssertAllCopied<Tag>((target, source) => target.ApplyEditableFrom(source), TagNotEditable);

    [Fact]
    public void DeviceCopiesEveryEditableField() =>
        AssertAllCopied<Device>((target, source) => target.ApplyEditableFrom(source), DeviceNotEditable);

    [Fact]
    public void AlarmRuleCopiesEveryEditableField() =>
        AssertAllCopied<AlarmRule>((target, source) => target.ApplyEditableFrom(source), AlarmRuleNotEditable);

    private static void AssertAllCopied<T>(Action<T, T> copy, IReadOnlySet<string> excluded) where T : new()
    {
        var editable = typeof(T).GetProperties()
            .Where(p => p.CanWrite && p.CanRead && !excluded.Contains(p.Name))
            .ToArray();

        var source = new T();
        foreach (var property in editable) property.SetValue(source, Sample(property));

        var target = new T();
        copy(target, source);

        foreach (var property in editable)
        {
            var expected = property.GetValue(source);
            var actual = property.GetValue(target);
            Assert.True(object.Equals(expected, actual),
                $"{typeof(T).Name}.{property.Name} 没有被 ApplyEditableFrom 复制（期望 {expected}，实际 {actual}）。" +
                "如果这是刻意排除，请把它加进本用例的排除清单并写明原因。");
        }
    }

    private static object Sample(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string)) return "v-" + property.Name;
        if (type == typeof(int)) return 7;
        if (type == typeof(long)) return 7L;
        if (type == typeof(double)) return 1.5d;
        if (type == typeof(float)) return 1.5f;
        if (type == typeof(bool)) return true;
        if (type == typeof(short)) return (short)3;
        if (type == typeof(byte)) return (byte)3;
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1)!;
        }
        throw new InvalidOperationException(
            $"未覆盖的属性类型 {property.DeclaringType?.Name}.{property.Name}（{type.Name}）：请在 Sample 里补充取值，" +
            "否则新增字段的复制遗漏不会被发现。");
    }
}
