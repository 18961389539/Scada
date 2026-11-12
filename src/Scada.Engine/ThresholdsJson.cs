using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scada.Engine;

// 阈值配置 JSON 合并：以默认值为底覆盖已保存字段；保存后又非法（例如手工改库）时回退默认值。
internal static class ThresholdsJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static T Parse<T>(string? json, T defaults, Func<T, string?> validate)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return defaults;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var merged = JsonSerializer.SerializeToNode(defaults, Json)!.AsObject();
            foreach (var p in doc.RootElement.EnumerateObject())
                if (merged.ContainsKey(p.Name)) merged[p.Name] = JsonNode.Parse(p.Value.GetRawText());
            var parsed = merged.Deserialize<T>(Json)!;
            return validate(parsed) is null ? parsed : defaults;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return defaults; }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
}