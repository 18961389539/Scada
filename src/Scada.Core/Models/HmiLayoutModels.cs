namespace Scada.Core.Models;

public sealed class HmiLayoutDocument
{
    public List<HmiMonitor> Monitors { get; set; } = [];
}

public sealed class HmiMonitor
{
    public string Name { get; set; } = "";
    public int Columns { get; set; } = 12;
    public int Rows { get; set; } = 12;
    public List<HmiRegion> Regions { get; set; } = [];
}

public sealed class HmiRegion
{
    public string Id { get; set; } = "";
    // screen：工艺画面（可在区域内导航，带前进/后退）；alarmBanner：最新报警横幅；alarmList：活动报警列表；navigation：画面目录。
    public string Kind { get; set; } = "screen";
    public int? ScreenId { get; set; }
    public string? Title { get; set; }
    public int Row { get; set; } = 1;
    public int Col { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;
}

public static class HmiLayoutValidator
{
    public static readonly string[] Kinds = ["screen", "alarmBanner", "alarmList", "navigation"];

    public static string? Validate(HmiLayoutDocument? doc, IReadOnlySet<int>? publishedScreens = null)
    {
        if (doc?.Monitors is null || doc.Monitors.Count is 0 or > 8) return "显示器须为 1～8 个";
        for (var m = 0; m < doc.Monitors.Count; m++)
        {
            var mon = doc.Monitors[m]; var label = $"显示器 {m + 1}";
            if (mon is null) return $"{label}为空";
            if ((mon.Name ?? "").Length > 40) return $"{label}名称过长";
            if (mon.Columns is < 1 or > 24 || mon.Rows is < 1 or > 24) return $"{label}网格须为 1～24 行列";
            if (mon.Regions is null || mon.Regions.Count is 0 or > 16) return $"{label}区域须为 1～16 个";
            var ids = new HashSet<string>();
            foreach (var r in mon.Regions)
            {
                if (r is null || string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 40 || !ids.Add(r.Id)) return $"{label}区域编号无效或重复";
                if (!Kinds.Contains(r.Kind)) return $"{label}区域 {r.Id} 类型无效";
                if (r.Row < 1 || r.Col < 1 || r.RowSpan < 1 || r.ColSpan < 1 || r.Row + r.RowSpan - 1 > mon.Rows || r.Col + r.ColSpan - 1 > mon.Columns) return $"{label}区域 {r.Id} 超出网格";
                if ((r.Title ?? "").Length > 60) return $"{label}区域 {r.Id} 标题过长";
                if (r.Kind == "screen" && (r.ScreenId is not > 0 || publishedScreens is not null && !publishedScreens.Contains(r.ScreenId.Value))) return $"{label}区域 {r.Id} 须选择已发布的画面";
            }
            foreach (var a in mon.Regions)
                foreach (var b in mon.Regions)
                    if (!ReferenceEquals(a, b) && a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan && a.Col < b.Col + b.ColSpan && b.Col < a.Col + a.ColSpan) return $"{label}区域 {a.Id} 与 {b.Id} 重叠";
        }
        return null;
    }
}
