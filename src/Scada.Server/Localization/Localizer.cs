using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Scada.Server.Localization;

// 后端提示本地化：源文案为中文，英文对照表 messages.en-US.json（由 tools/backend-i18n-extract.py 抽取后翻译）。
// 含插值的提示以 {0}{1} 占位匹配；找不到对照时原样返回中文（tests 中有覆盖率检查）。
public static class Localizer
{
    public const string ZhCn = "zh-CN", EnUs = "en-US", LocaleHeader = "X-Scada-Locale";
    private static readonly Lazy<(FrozenDictionary<string, string> Exact, (Regex Pattern, string En)[] Patterns, (string Zh, string En)[] Prefixes)> Table = new(Load);

    public static IReadOnlyDictionary<string, string> Messages => Table.Value.Exact;

    public static string Normalize(string? locale) =>
        locale is not null && locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? EnUs : ZhCn;

    // 请求语言：前端显式头优先，其次 Accept-Language 首选项；缺省中文。
    public static string RequestLocale(HttpContext http)
    {
        if (http.Request.Headers[LocaleHeader].FirstOrDefault() is { Length: > 0 } explicitLocale) return Normalize(explicitLocale);
        var accept = http.Request.Headers.AcceptLanguage.FirstOrDefault();
        return accept is { Length: > 0 } ? Normalize(accept.Split(',')[0].Trim()) : ZhCn;
    }

    public static string T(string text, string locale)
    {
        if (locale != EnUs || string.IsNullOrEmpty(text) || !HasHan(text)) return text;
        var table = Table.Value;
        if (table.Exact.TryGetValue(text, out var exact)) return exact;
        foreach (var (pattern, en) in table.Patterns)
        {
            var m = pattern.Match(text);
            if (m.Success) return Regex.Replace(en, @"\{(\d+)\}", x => int.TryParse(x.Groups[1].Value, out var i) && i + 1 < m.Groups.Count ? T(m.Groups[i + 1].Value, locale) : x.Value);
        }
        // 前缀 + 动态细节（如“预检查未通过，未下发任何参数：……”）
        foreach (var (zh, en) in table.Prefixes)
            if (text.StartsWith(zh, StringComparison.Ordinal)) return en + T(text[zh.Length..], locale);
        // 多条提示以分号拼接时逐段翻译
        foreach (var sep in new[] { "；", "; " })
            if (text.Contains(sep)) return string.Join("; ", text.Split(sep).Select(x => T(x, locale)));
        return text;
    }

    public static string T(this HttpContext http, string text) => T(text, RequestLocale(http));

    private static bool HasHan(string s) { foreach (var c in s) if (c is >= '\u4e00' and <= '\u9fff') return true; return false; }

    private static (FrozenDictionary<string, string>, (Regex, string)[], (string, string)[]) Load()
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("Scada.Server.Localization.messages.en-US.json");
        var map = stream is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        var patterns = map.Where(x => Regex.IsMatch(x.Key, @"\{\w+\}"))
            .OrderByDescending(x => Regex.Replace(x.Key, @"\{\w+\}", "").Length)
            .Select(x => (new Regex("^" + string.Concat(Regex.Split(x.Key, @"(\{\w+\})").Select(p => Regex.IsMatch(p, @"^\{\w+\}$") ? "(.*?)" : Regex.Escape(p))) + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)),
                Renumber(x.Key, x.Value))).ToArray();
        var prefixes = map.Where(x => !x.Key.Contains('{') && (x.Key.EndsWith('：') || x.Key.EndsWith(": ") || x.Key.EndsWith('，')))
            .OrderByDescending(x => x.Key.Length).Select(x => (x.Key, x.Value)).ToArray();
        return (map.ToFrozenDictionary(), patterns, prefixes);
    }

    // 命名占位（如 {Name}）按出现顺序换成 {0}{1}，与正则捕获组对应。
    private static string Renumber(string zh, string en)
    {
        var names = Regex.Matches(zh, @"\{(\w+)\}").Select(m => m.Groups[1].Value).ToList();
        return Regex.Replace(en, @"\{(\w+)\}", m => names.IndexOf(m.Groups[1].Value) is var i and >= 0 ? "{" + i + "}" : m.Value);
    }
}

// 英文界面时把 /api JSON 响应中的 message/title/detail/error 字段译为英文（状态码与结构不变）。
public sealed class ResponseLocalization(RequestDelegate next)
{
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase) { "message", "title", "detail", "error" };
    private const int MaxBytes = 512 * 1024;

    public async Task InvokeAsync(HttpContext http)
    {
        var locale = Localizer.RequestLocale(http);
        if (locale != Localizer.EnUs || !http.Request.Path.StartsWithSegments("/api")) { await next(http); return; }
        var original = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try { await next(http); }
        finally { http.Response.Body = original; }
        buffer.Position = 0;
        if (http.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && buffer.Length is > 0 and < MaxBytes)
        {
            try
            {
                var node = JsonNode.Parse(buffer.ToArray());
                if (node is not null && Translate(node, locale))
                {
                    var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    http.Response.ContentLength = bytes.Length;
                    await original.WriteAsync(bytes, http.RequestAborted);
                    return;
                }
            }
            catch (JsonException) { }
            buffer.Position = 0;
        }
        await buffer.CopyToAsync(original, http.RequestAborted);
    }

    private static bool Translate(JsonNode node, string locale)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is JsonValue v && Fields.Contains(key) && v.TryGetValue<string>(out var s))
                    {
                        var t = Localizer.T(s, locale);
                        if (t != s) { obj[key] = t; changed = true; }
                    }
                    else if (value is JsonObject or JsonArray && key is "errors" or "issues" or "results" or "steps" or "items" or "problems") changed |= Translate(value, locale);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s)) { var t = Localizer.T(s, locale); if (t != s) { arr[i] = t; changed = true; } }
                    else if (arr[i] is { } child) changed |= Translate(child, locale);
                }
                break;
        }
        return changed;
    }
}
