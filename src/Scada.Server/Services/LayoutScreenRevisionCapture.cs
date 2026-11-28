using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Services;

/// <summary>布局发布时记下当时各引用画面的已发布修订，预览时优先按修订加载，否则回退到发布时间查找。</summary>
public static class LayoutScreenRevisionCapture
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<string> CaptureAsync(ConfigDbContext db, string documentJson, CancellationToken cancellationToken)
    {
        HmiLayoutDocument? document;
        try { document = JsonSerializer.Deserialize<HmiLayoutDocument>(documentJson, Json); }
        catch (JsonException) { return "{}"; }
        return await CaptureAsync(db, document, cancellationToken);
    }

    public static async Task<string> CaptureAsync(ConfigDbContext db, HmiLayoutDocument? document, CancellationToken cancellationToken)
    {
        var ids = HmiLayoutDocument.ReferencedScreenIds(document).Distinct().ToList();
        if (ids.Count == 0) return "{}";
        var rows = await db.ProcessScreens.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.PublishedRevision != null)
            .Select(x => new { x.Id, x.PublishedRevision })
            .ToListAsync(cancellationToken);
        var map = rows.ToDictionary(x => x.Id.ToString(), x => x.PublishedRevision!.Value);
        return JsonSerializer.Serialize(map);
    }

    public static Dictionary<int, int> Parse(string? json)
    {
        var result = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (raw is null) return result;
            foreach (var (key, value) in raw)
            {
                if (int.TryParse(key, out var id) && id > 0 && value > 0) result[id] = value;
            }
        }
        catch (JsonException)
        {
            // 历史脏数据忽略，预览回退到发布时间查找。
        }
        return result;
    }
}
