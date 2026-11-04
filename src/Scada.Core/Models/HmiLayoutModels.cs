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
    // Target browser viewport for layout previews; runtime still follows its actual window size.
    public int? PreviewWidth { get; set; }
    public int? PreviewHeight { get; set; }
    public List<HmiRegion> Regions { get; set; } = [];
}

public sealed class HmiRegion
{
    public string Id { get; set; } = "";
    // screen：工艺画面（可在区域内导航，带前进/后退）；alarmBanner：最新报警横幅；alarmList：活动报警列表；navigation：画面目录。
    public string Kind { get; set; } = "screen";
    public int? ScreenId { get; set; }
    // 报警列表的可选范围，最多设置一项。未设置时列表是全站报警，运行页不写成「分区」。
    public int? AlarmAreaId { get; set; }
    public int? AlarmDeviceId { get; set; }
    public int? AlarmScreenId { get; set; }
    public string? Title { get; set; }
    public int Row { get; set; } = 1;
    public int Col { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;
}

public static class HmiLayoutValidator
{
    public static readonly string[] Kinds = ["screen", "alarmBanner", "alarmList", "navigation"];

    // 草稿允许暂缺画面、空区域、重叠及越界；仍限制文档结构和数量，防止保存不可解析的内容。
    public static string? ValidateDraft(HmiLayoutDocument? doc)
    {
        if (doc?.Monitors is null || doc.Monitors.Count is 0 or > 8) return "显示器须为 1～8 个";
        for (var m = 0; m < doc.Monitors.Count; m++)
        {
            var mon = doc.Monitors[m]; var label = $"显示器 {m + 1}";
            if (mon is null) return $"{label}为空";
            if ((mon.Name ?? "").Length > 40) return $"{label}名称过长";
            if (mon.Columns is < 1 or > 24 || mon.Rows is < 1 or > 24) return $"{label}网格须为 1～24 行列";
            if ((mon.PreviewWidth is null) != (mon.PreviewHeight is null) ||
                mon.PreviewWidth is < 320 or > 7680 || mon.PreviewHeight is < 240 or > 4320)
                return $"{label}预览尺寸须同时设置，宽度 320～7680、高度 240～4320 像素";
            if (mon.Regions is null || mon.Regions.Count > 16) return $"{label}区域最多 16 个";
            foreach (var r in mon.Regions)
            {
                if (r is null) return $"{label}区域为空";
                if ((r.Id ?? "").Length > 40) return $"{label}区域编号过长";
                if (!Kinds.Contains(r.Kind)) return $"{label}区域 {r.Id} 类型无效";
                if ((r.Title ?? "").Length > 60) return $"{label}区域 {r.Id} 标题过长";
            }
        }
        return null;
    }

    public static string? Validate(HmiLayoutDocument? doc, IReadOnlySet<int>? publishedScreens = null)
    {
        if (ValidateDraft(doc) is string error) return error;
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
                if (r.Row < 1 || r.Col < 1 || r.RowSpan < 1 || r.ColSpan < 1 || (long)r.Row + r.RowSpan - 1 > mon.Rows || (long)r.Col + r.ColSpan - 1 > mon.Columns) return $"{label}区域 {r.Id} 超出网格";
                if ((r.Title ?? "").Length > 60) return $"{label}区域 {r.Id} 标题过长";
                if (r.Kind == "screen" && (r.ScreenId is not > 0 || publishedScreens is not null && !publishedScreens.Contains(r.ScreenId.Value))) return $"{label}区域 {r.Id} 须选择已发布的画面";
                if (r.AlarmAreaId is <= 0 || r.AlarmDeviceId is <= 0 || r.AlarmScreenId is <= 0) return $"{label}区域 {r.Id} 的报警范围无效";
                if (r.Kind != "alarmList" && (r.AlarmAreaId != null || r.AlarmDeviceId != null || r.AlarmScreenId != null)) return $"{label}区域 {r.Id} 的报警范围只适用于报警列表";
                if ((r.AlarmAreaId != null ? 1 : 0) + (r.AlarmDeviceId != null ? 1 : 0) + (r.AlarmScreenId != null ? 1 : 0) > 1) return $"{label}区域 {r.Id} 只能绑定一种报警范围";
            }
            foreach (var a in mon.Regions)
                foreach (var b in mon.Regions)
                    if (!ReferenceEquals(a, b) && a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan && a.Col < b.Col + b.ColSpan && b.Col < a.Col + a.ColSpan) return $"{label}区域 {a.Id} 与 {b.Id} 重叠";
            var leading = 0;
            while (leading < mon.Rows)
            {
                var row = leading + 1;
                var occupied = mon.Regions.Where(r => r.Row <= row && r.Row + r.RowSpan > row).ToList();
                if (occupied.Count == 0 || occupied.Any(r => r.Kind != "alarmBanner")) break;
                leading++;
            }
            if (mon.Regions.Any(r => r.Kind == "alarmBanner" && r.Row + r.RowSpan - 1 > leading)) return $"{label}报警横幅只能占据顶部且整行都是横幅";
        }
        return null;
    }
}
