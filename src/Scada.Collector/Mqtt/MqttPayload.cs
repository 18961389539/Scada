using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scada.Core.Enums;
using Scada.Core.Protocols;

namespace Scada.Collector.Mqtt;

/// <summary>通用 MQTT 载荷与数值的互相转换（纯函数，便于单测）。</summary>
public static class MqttPayload
{
    public static bool TryValue(byte[] payload, string? path, out double value, out string? error)
    {
        value = 0; error = null;
        var text = Encoding.UTF8.GetString(payload).Trim();
        if (path is null)
        {
            if (TryScalar(text, out value)) return true;
            try { var node = JsonNode.Parse(text); if (node is JsonValue v && TryJson(v, out value)) return true; }
            catch (JsonException) { }
            error = "载荷不是数值或 true/false"; return false;
        }
        JsonNode? current;
        try { current = JsonNode.Parse(text); }
        catch (JsonException) { error = "载荷不是有效 JSON"; return false; }
        foreach (var segment in JsonPath.Segments(path))
        {
            current = segment switch
            {
                string name when current is JsonObject o => o[name],
                int index when current is JsonArray a && index < a.Count => a[index],
                _ => null
            };
            if (current is null) { error = $"JSON 路径 {path} 不存在"; return false; }
        }
        if (current is JsonValue jv && TryJson(jv, out value)) return true;
        error = $"JSON 路径 {path} 的值不是数值或布尔"; return false;
    }

    private static bool TryJson(JsonValue v, out double value)
    {
        value = 0;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number: value = v.GetValue<double>(); return double.IsFinite(value);
            case JsonValueKind.True: value = 1; return true;
            case JsonValueKind.False: value = 0; return true;
            case JsonValueKind.String: return TryScalar(v.GetValue<string>(), out value);
            default: return false;
        }
    }

    private static bool TryScalar(string text, out double value)
    {
        value = 0;
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("on", StringComparison.OrdinalIgnoreCase)) { value = 1; return true; }
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("off", StringComparison.OrdinalIgnoreCase)) { value = 0; return true; }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    /// <summary>解析时间戳：数字（≥1e11 视为毫秒，否则秒）或 ISO 8601 字符串。</summary>
    public static long? Timestamp(byte[] payload, string? path)
    {
        if (path is null) return null;
        try
        {
            JsonNode? current = JsonNode.Parse(payload);
            foreach (var segment in JsonPath.Segments(path))
                current = segment switch { string n when current is JsonObject o => o[n], int i when current is JsonArray a && i < a.Count => a[i], _ => null };
            if (current is not JsonValue v) return null;
            if (v.GetValueKind() == JsonValueKind.Number) { var n = v.GetValue<double>(); return (long)(n >= 1e11 ? n : n * 1000); }
            if (v.GetValueKind() == JsonValueKind.String && DateTimeOffset.TryParse(v.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) return dt.ToUnixTimeMilliseconds();
        }
        catch (JsonException) { }
        return null;
    }

    public static byte[] Encode(double value, TagDataType type, string? writePath)
    {
        JsonNode scalar = type == TagDataType.Bool ? JsonValue.Create(value != 0)
            : type is TagDataType.Float or TagDataType.Double ? JsonValue.Create(value) : JsonValue.Create((long)value);
        if (writePath is null) return Encoding.UTF8.GetBytes(scalar.ToJsonString());
        JsonNode root = new JsonObject();
        var segments = JsonPath.Segments(writePath);
        JsonNode cursor = root;
        for (var i = 0; i < segments.Count; i++)
        {
            var last = i == segments.Count - 1;
            if (segments[i] is not string name || cursor is not JsonObject obj) throw new InvalidOperationException("写入路径只支持对象属性（不支持数组下标）");
            if (last) obj[name] = scalar;
            else { var next = new JsonObject(); obj[name] = next; cursor = next; }
        }
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    public static bool SameValue(double a, double b) => a == b || Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
}
