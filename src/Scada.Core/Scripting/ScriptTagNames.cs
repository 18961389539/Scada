using System.Globalization;
using System.Text;
using Scada.Core.Calculations;

namespace Scada.Core.Scripting;

public readonly record struct ScriptTagRef(int Id, string Name, string? DeviceName);

// 脚本白名单按测点名或编号填写。编号仍是运行时的键；名称在保存和导入时解析。
public static class ScriptTagNames
{
    public static string Key(ScriptTagRef tag) => string.IsNullOrEmpty(tag.DeviceName) ? "@" + tag.Name : tag.DeviceName + "/" + tag.Name;

    public static Dictionary<string, int> ForScript(IEnumerable<ScriptTagRef> whitelist)
    {
        var rows = whitelist.ToArray();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(x => x.Name, StringComparer.Ordinal))
            if (group.Count() == 1) map[group.Key] = group.First().Id;
        foreach (var tag in rows) map[Key(tag)] = tag.Id;
        return map;
    }

    public static int[] Resolve(string? text, IReadOnlyList<ScriptTagRef> catalog)
    {
        var tokens = (text ?? "").Split([',', ' ', ';', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return [];
        var byId = catalog.ToDictionary(x => x.Id);
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tag in catalog) byKey[Key(tag)] = tag.Id;
        var byName = catalog.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var ids = new List<int>();
        foreach (var token in tokens)
        {
            if (token.Contains('/'))
            {
                if (!byKey.TryGetValue(token, out var keyId)) throw new TagExpressionException($"测点 {token} 不存在");
                ids.Add(keyId);
                continue;
            }
            var number = token.StartsWith('#') ? token[1..] : token;
            if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && number.Length == token.TrimStart('#').Length)
            {
                if (byId.ContainsKey(id)) { ids.Add(id); continue; }
                if (!token.StartsWith('#') && byName.TryGetValue(token, out var named) && named.Length == 1) { ids.Add(named[0].Id); continue; }
                throw new TagExpressionException($"测点 #{id} 不存在");
            }
            if (token.StartsWith('@') && byKey.TryGetValue(token, out var atId)) { ids.Add(atId); continue; }
            if (byName.TryGetValue(token, out var matches))
            {
                if (matches.Length != 1) throw new TagExpressionException($"测点名 {token} 不唯一，请写成 设备/名称");
                ids.Add(matches[0].Id);
                continue;
            }
            throw new TagExpressionException($"测点 {token} 不存在");
        }
        return ids.Distinct().Order().ToArray();
    }

    // 只改写 context.Read / ReadSample / Write 的整数参数，跳过字符串、字符和注释。
    public static string RebindCalls(string source, IReadOnlyDictionary<int, int> oldToNew)
    {
        if (string.IsNullOrEmpty(source) || oldToNew.Count == 0 || oldToNew.All(pair => pair.Key == pair.Value)) return source;
        var builder = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length;)
        {
            var skipped = SkipNonCode(source, i);
            if (skipped > i) { builder.Append(source, i, skipped - i); i = skipped; continue; }
            if (TryCallNumber(source, i, out var numberStart, out var numberEnd) &&
                int.TryParse(source.AsSpan(numberStart, numberEnd - numberStart), NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                oldToNew.TryGetValue(id, out var mapped))
            {
                builder.Append(source, i, numberStart - i);
                builder.Append(mapped.ToString(CultureInfo.InvariantCulture));
                i = numberEnd;
                continue;
            }
            builder.Append(source[i]);
            i++;
        }
        return builder.ToString();
    }

    // 取出 context.Read / ReadSample / Write / History 的字符串参数，供引用列表对照当前测点名。
    public static List<string> QuotedArguments(string? source)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(source)) return found;
        for (var i = 0; i < source.Length;)
        {
            var skipped = SkipNonCode(source, i);
            if (skipped > i) { i = skipped; continue; }
            if (TryCallString(source, i, out var value, out var next)) { found.Add(value); i = next; continue; }
            i++;
        }
        return found;
    }

    private static int SkipNonCode(string source, int i)
    {
        if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
        {
            var end = source.IndexOf('\n', i);
            return end < 0 ? source.Length : end;
        }
        if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
        {
            var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return end < 0 ? source.Length : end + 2;
        }
        if (source[i] == '@' && i + 1 < source.Length && source[i + 1] == '"') return SkipVerbatim(source, i + 1);
        if (source[i] == '$' && i + 1 < source.Length && source[i + 1] == '"') return SkipRegular(source, i + 1);
        if (source[i] == '"') return SkipRegular(source, i);
        if (source[i] == '\'') return SkipRegular(source, i);
        return i;
    }

    private static int SkipRegular(string source, int quote) 
    {
        var mark = source[quote];
        for (var i = quote + 1; i < source.Length; i++)
        {
            if (source[i] == '\\') { i++; continue; }
            if (source[i] == mark) return i + 1;
        }
        return source.Length;
    }

    private static int SkipVerbatim(string source, int quote)
    {
        for (var i = quote + 1; i < source.Length; i++)
        {
            if (source[i] != '"') continue;
            if (i + 1 < source.Length && source[i + 1] == '"') { i++; continue; }
            return i + 1;
        }
        return source.Length;
    }

    private static bool TryCallNumber(string source, int i, out int numberStart, out int numberEnd)
    {
        numberStart = numberEnd = 0;
        ReadOnlySpan<char> text = source.AsSpan(i);
        if (!text.StartsWith("context")) return false;
        var j = i + "context".Length;
        while (j < source.Length && char.IsWhiteSpace(source[j])) j++;
        if (j >= source.Length || source[j] != '.') return false;
        j++;
        while (j < source.Length && char.IsWhiteSpace(source[j])) j++;
        string? method = null;
        foreach (var candidate in new[] { "ReadSample", "Read", "Write" })
        {
            if (!source.AsSpan(j).StartsWith(candidate)) continue;
            var after = j + candidate.Length;
            if (after < source.Length && (char.IsLetterOrDigit(source[after]) || source[after] == '_')) continue;
            method = candidate;
            j = after;
            break;
        }
        if (method is null) return false;
        while (j < source.Length && char.IsWhiteSpace(source[j])) j++;
        if (j >= source.Length || source[j] != '(') return false;
        j++;
        while (j < source.Length && char.IsWhiteSpace(source[j])) j++;
        if (j >= source.Length || !char.IsDigit(source[j])) return false;
        numberStart = j;
        while (j < source.Length && char.IsDigit(source[j])) j++;
        numberEnd = j;
        while (j < source.Length && char.IsWhiteSpace(source[j])) j++;
        if (method == "Write") return j < source.Length && source[j] == ',';
        return j < source.Length && source[j] == ')';
    }

    private static bool TryCallString(string source, int i, out string value, out int next)
    {
        value = ""; next = i;
        if (!source.AsSpan(i).StartsWith("context")) return false;
        var j = i + "context".Length;
        while (j < source.Length && char.IsWhiteSpace(source[j])) j++;
        if (j >= source.Length || source[j] != '.') return false;
        j++;
        while (j < source.Length && char.IsWhiteSpace(source[j])) j++;
        string? method = null;
        foreach (var candidate in new[] { "ReadSample", "History", "Read", "Write" })
        {
            if (!source.AsSpan(j).StartsWith(candidate)) continue;
            var after = j + candidate.Length;
            if (after < source.Length && (char.IsLetterOrDigit(source[after]) || source[after] == '_')) continue;
            method = candidate; j = after; break;
        }
        if (method is null) return false;
        while (j < source.Length && char.IsWhiteSpace(source[j])) j++;
        if (j >= source.Length || source[j] != '(') return false;
        j++;
        while (j < source.Length && char.IsWhiteSpace(source[j])) j++;
        if (j >= source.Length || source[j] != '"') return false;
        var start = j + 1;
        var end = SkipRegular(source, j);
        if (end <= start || source[end - 1] != '"') return false;
        value = source[start..(end - 1)].Replace("\\\"", "\"", StringComparison.Ordinal);
        next = end;
        return true;
    }
}
