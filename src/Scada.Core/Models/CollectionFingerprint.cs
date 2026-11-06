using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Scada.Core.Entities;

namespace Scada.Core.Models;

// 采集指纹：把"建立连接与读数"依赖的字段折成一个稳定字符串，用来判断一次配置保存是否真的需要断开重连现场设备。
// 属性用反射枚举而不是手写清单：新增协议参数后不会因为漏加字段导致改了配置却不重载（那种漏法在运行时几乎没有征兆）。
public static class CollectionFingerprint
{
    private static readonly PropertyInfo[] DeviceProperties = ScalarProperties(typeof(Device));
    private static readonly PropertyInfo[] TagProperties = ScalarProperties(typeof(Tag));
    private static readonly PropertyInfo[] AreaProperties = ScalarProperties(typeof(Area));
    private static readonly PropertyInfo[] RuleProperties = ScalarProperties(typeof(AlarmRule));

    // 只有启用的设备和物理测点参与：停用设备、计算测点、区域与报警规则的变化不影响现场连接。
    public static string Of(IEnumerable<Device> devices, IEnumerable<Tag> tags)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendAll(hash, DeviceProperties, devices.OrderBy(x => x.Id));
        AppendAll(hash, TagProperties, tags.Where(x => x.Kind == Core.Enums.TagKind.Physical && x.DeviceId.HasValue).OrderBy(x => x.Id));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string OfDevice(Device device, IEnumerable<Tag> tags) => Of([device], tags);

    // 全量指纹：配置库任何一处改动都会变，用于 Version（报警、报表、画面等消费方据此刷新）。
    public static string OfAll(IEnumerable<Device> devices, IEnumerable<Tag> tags, IEnumerable<Area> areas, IEnumerable<AlarmRule> rules)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendAll(hash, DeviceProperties, devices.OrderBy(x => x.Id));
        AppendAll(hash, TagProperties, tags.OrderBy(x => x.Id));
        AppendAll(hash, AreaProperties, areas.OrderBy(x => x.Id));
        AppendAll(hash, RuleProperties, rules.OrderBy(x => x.Id));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static PropertyInfo[] ScalarProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.GetIndexParameters().Length == 0 && IsScalar(x.PropertyType))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive || underlying.IsEnum
            || underlying == typeof(string) || underlying == typeof(decimal)
            || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)
            || underlying == typeof(TimeSpan) || underlying == typeof(Guid);
    }

    private static void AppendAll<T>(IncrementalHash hash, PropertyInfo[] properties, IEnumerable<T> entities)
    {
        foreach (var entity in entities)
        {
            if (entity is not null) Append(hash, properties, entity);
        }
    }

    private static void Append(IncrementalHash hash, PropertyInfo[] properties, object entity)
    {
        foreach (var property in properties)
        {
            // 属性名一起进哈希：字段重排或新增不会与旧值混淆。
            AppendField(hash, property.Name);
            var value = property.GetValue(entity);
            AppendField(hash, value switch
            {
                null => "\u0000null",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? ""
            });
        }
    }

    private static void AppendField(IncrementalHash hash, string text)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        hash.AppendData([(byte)0x1f]);
    }
}
