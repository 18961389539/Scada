using System.Text.Json;

namespace Scada.Core.Kiosk;

public static class KioskPlaylistRules
{
    public const int MinIntervalSeconds = 2;
    public const int MaxIntervalSeconds = 600;
    public const int DefaultIntervalSeconds = 20;
    public const int MaxScreens = 50;

    public static int ClampInterval(int seconds) =>
        Math.Clamp(seconds <= 0 ? DefaultIntervalSeconds : seconds, MinIntervalSeconds, MaxIntervalSeconds);

    public static bool TryParseScreenIds(string? json, out List<int> ids, out string? error)
    {
        ids = [];
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "请至少选择一个已发布画面";
            return false;
        }
        try
        {
            var raw = JsonSerializer.Deserialize<int[]>(json);
            if (raw is null)
            {
                error = "画面顺序无效";
                return false;
            }
            var seen = new HashSet<int>();
            foreach (var id in raw)
            {
                if (id <= 0)
                {
                    error = "画面编号无效";
                    return false;
                }
                if (!seen.Add(id)) continue;
                ids.Add(id);
            }
        }
        catch (JsonException)
        {
            error = "画面顺序无效";
            return false;
        }
        if (ids.Count == 0)
        {
            error = "请至少选择一个已发布画面";
            return false;
        }
        if (ids.Count > MaxScreens)
        {
            error = $"播放列表最多包含 {MaxScreens} 个画面";
            return false;
        }
        return true;
    }

    public static string SerializeScreenIds(IEnumerable<int> ids)
    {
        var unique = new List<int>();
        var seen = new HashSet<int>();
        foreach (var id in ids)
        {
            if (id <= 0 || !seen.Add(id)) continue;
            unique.Add(id);
        }
        return JsonSerializer.Serialize(unique);
    }
}
