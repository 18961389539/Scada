using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scada.Core.Security;

// 配置变更的字段级差分：{"op":"create|update|delete","changes":{"字段":{"old":..,"new":..}}}；敏感字段只标记已变化。
public static class AuditDiff
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 历史名单保留作为兜底：审计对象可能是 JsonElement（工程包回放），拿不到类型信息。
    // 实体上新增的密钥字段应该加 [Sensitive]，由 SensitiveNames 反射收集，不必改这里。
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "passwordHash", "password", "opcPasswordProtected", "opcPassword", "tokenHash", "secret", "secretProtected"
    };

    private static readonly ConcurrentDictionary<Type, HashSet<string>> Cache = new();

    private static HashSet<string> SensitiveNames(Type? type)
    {
        if (type is null) return Known;
        return Cache.GetOrAdd(type, static t =>
        {
            var names = new HashSet<string>(Known, StringComparer.OrdinalIgnoreCase);
            foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.IsDefined(typeof(SensitiveAttribute), inherit: true)) names.Add(property.Name);
            }
            return names;
        });
    }

    public static string Compute(object? before, object? after)
    {
        var oldMap = Flatten(before);
        var newMap = Flatten(after);
        var sensitive = SensitiveNames(before?.GetType() ?? after?.GetType());
        var changes = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in oldMap.Keys.Union(newMap.Keys))
        {
            oldMap.TryGetValue(key, out var o);
            newMap.TryGetValue(key, out var n);
            if (JsonEquals(o, n)) continue;
            changes[key] = sensitive.Contains(key)
                ? new { old = IsNull(o) ? null : "***", @new = IsNull(n) ? null : "***" }
                : new { old = o, @new = n };
        }

        var op = before is null ? "create" : after is null ? "delete" : "update";
        return JsonSerializer.Serialize(new { op, changes }, Json);
    }

    private static bool IsNull(JsonElement? e) => e is null || e.Value.ValueKind == JsonValueKind.Null;

    private static Dictionary<string, JsonElement?> Flatten(object? value)
    {
        var map = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (value is null) return map;
        var element = value is JsonElement e ? e : JsonSerializer.SerializeToElement(value, value.GetType(), Json);
        if (element.ValueKind != JsonValueKind.Object) { map["value"] = element; return map; }
        foreach (var property in element.EnumerateObject()) map[property.Name] = property.Value.Clone();
        return map;
    }

    private static bool JsonEquals(JsonElement? a, JsonElement? b)
    {
        if (IsNull(a)) return IsNull(b);
        if (IsNull(b)) return false;
        return a!.Value.GetRawText() == b!.Value.GetRawText();
    }
}
