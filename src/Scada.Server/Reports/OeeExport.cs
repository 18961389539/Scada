using System.Globalization;
using ClosedXML.Excel;
using Scada.Engine.Oee;
using Scada.Server.Localization;
using SkiaSharp;

namespace Scada.Server.Reports;

public static class OeeExport
{
    public sealed record Section(string Title, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows);

    public static List<Section> Sections(OeeReport report, TimeZoneInfo zone, string locale)
    {
        string L(string s) => Localizer.T(s, locale);
        string Pct(double? v) => v is null ? "" : (v.Value * 100).ToString("0.00", CultureInfo.InvariantCulture);
        string Num(double? v, string f = "0.##") => v is null ? "" : v.Value.ToString(f, CultureInfo.InvariantCulture);
        string Ts(long ms) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone).ToString("yyyy-MM-dd HH:mm:ss");
        var k = report.Kpi;
        return
        [
            new(L("KPI 总览"), [L("指标"), L("值")],
            [
                [L("可用率 %"), Pct(k.Availability)], [L("表现率 %"), Pct(k.Performance)], [L("质量率 %"), Pct(k.Quality)], [L("OEE %"), Pct(k.Oee)],
                [L("计划小时"), Num(k.PlannedMs / (double)OeeMath.HourMs)], [L("运行小时"), Num(k.RunMs / (double)OeeMath.HourMs)], [L("停机小时"), Num(k.DownMs / (double)OeeMath.HourMs)],
                [L("好品"), Num(k.Good)], [L("废品"), Num(k.Reject + k.Scrap)], [L("废品率 %"), Pct(k.ScrapRate)],
                [L("MTBF 小时"), Num(k.MtbfHours)], [L("MTTR 小时"), Num(k.MttrHours)], [L("故障次数"), k.FaultCount],
                [L("产量/目标 %"), Num(k.ProductionVsTargetPercent)],
                [L("性能超100%"), k.PerformanceOver100 ? L("是") : L("否")], [L("性能已封顶"), k.PerformanceClamped ? L("是") : L("否")]
            ]),
            new(L("停机帕累托"), [L("代码"), L("名称"), L("类别"), L("计划"), L("小时"), L("次数"), L("占比%"), L("累计%")],
                k.DowntimePareto.Select(d => (IReadOnlyList<object?>)[d.Code, d.Name, d.Category, d.IsPlanned ? L("是") : L("否"), Num(d.Hours), d.Count, d.Percent, d.CumulativePercent]).ToList()),
            new(L("按班次"), [L("班次"), L("可用率%"), L("表现率%"), L("质量率%"), L("OEE%"), L("好品"), L("总量")],
                k.ByShift.Select(r => (IReadOnlyList<object?>)[r.ShiftName ?? r.Label, Pct(r.Availability), Pct(r.Performance), Pct(r.Quality), Pct(r.Oee), Num(r.Good), Num(r.Total)]).ToList()),
            new(L("按产品"), [L("产品"), L("可用率%"), L("表现率%"), L("质量率%"), L("OEE%"), L("好品"), L("废品")],
                k.ByProduct.Select(r => (IReadOnlyList<object?>)[r.ProductName ?? r.Label, Pct(r.Availability), Pct(r.Performance), Pct(r.Quality), Pct(r.Oee), Num(r.Good), Num(r.Reject + r.Scrap)]).ToList()),
            new(L("按日"), [L("日期"), L("可用率%"), L("表现率%"), L("质量率%"), L("OEE%"), L("好品"), L("总量")],
                k.ByDay.Select(r => (IReadOnlyList<object?>)[r.Label, Pct(r.Availability), Pct(r.Performance), Pct(r.Quality), Pct(r.Oee), Num(r.Good), Num(r.Total)]).ToList()),
            new(L("故障明细"), [L("开始"), L("结束"), L("小时"), L("原因")],
                k.Faults.Select(f => (IReadOnlyList<object?>)[Ts(f.StartTs), Ts(f.EndTs), Num(f.Ms / (double)OeeMath.HourMs), f.ReasonName ?? ""]).ToList())
        ];
    }

    public static byte[] Xlsx(string title, IReadOnlyList<Section> sections)
    {
        using var workbook = new XLWorkbook();
        var used = new HashSet<string>();
        foreach (var section in sections)
        {
            var name = new string(section.Title.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
            if (name.Length > 28) name = name[..28];
            if (string.IsNullOrWhiteSpace(name)) name = "sheet";
            var baseName = name; var n = 1;
            while (!used.Add(name)) name = $"{baseName}_{++n}";
            var ws = workbook.Worksheets.Add(name);
            ws.Cell(1, 1).Value = title;
            ws.Range(1, 1, 1, Math.Max(1, section.Headers.Count)).Merge().Style.Font.SetBold();
            for (var c = 0; c < section.Headers.Count; c++) ws.Cell(3, c + 1).Value = section.Headers[c];
            ws.Row(3).Style.Font.Bold = true;
            for (var r = 0; r < section.Rows.Count; r++)
                for (var c = 0; c < section.Rows[r].Count; c++)
                {
                    var v = section.Rows[r][c];
                    var cell = ws.Cell(r + 4, c + 1);
                    switch (v)
                    {
                        case null: break;
                        case double d: cell.Value = d; break;
                        case int i: cell.Value = i; break;
                        case long l: cell.Value = l; break;
                        case bool b: cell.Value = b; break;
                        default: cell.Value = v.ToString(); break;
                    }
                }
            ws.Columns().AdjustToContents();
        }
        if (workbook.Worksheets.Count == 0) workbook.AddWorksheet("empty");
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    public static byte[] Pdf(string title, IReadOnlyList<Section> sections, string? fontPath, string footer)
    {
        using var typeface = ReportPdf.ResolveTypeface(fontPath);
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Title = title, Creator = "Scada", Producer = "Scada / SkiaSharp", Creation = DateTime.UtcNow }))
        {
            const float width = 842, height = 595, margin = 32, rowHeight = 15;
            using var titleFont = new SKFont(typeface, 14);
            using var head = new SKFont(typeface, 10) { Embolden = true };
            using var body = new SKFont(typeface, 7.5f);
            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var grid = new SKPaint { Color = new SKColor(0xB0, 0xB8, 0xC4), StrokeWidth = 0.5f, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { Color = new SKColor(0xE8, 0xEE, 0xF7), Style = SKPaintStyle.Fill };
            SKCanvas? canvas = null; float y = 0; var page = 0;
            void NewPage()
            {
                if (canvas is not null) { canvas.DrawText($"{footer} · {page}", width - margin, height - margin / 2, SKTextAlign.Right, body, ink); document.EndPage(); }
                canvas = document.BeginPage(width, height); page++;
                canvas.DrawText(title, margin, margin + 12, SKTextAlign.Left, titleFont, ink);
                y = margin + 30;
            }
            void Cell(string value, float x, float w, SKFont font, bool header)
            {
                if (header) canvas!.DrawRect(x, y, w, rowHeight, fill);
                canvas!.DrawRect(x, y, w, rowHeight, grid);
                var shown = value;
                while (shown.Length > 1 && font.MeasureText(shown) > w - 4) shown = shown[..^1];
                canvas.DrawText(shown, x + 3, y + rowHeight - 4, SKTextAlign.Left, font, ink);
            }
            NewPage();
            foreach (var section in sections)
            {
                if (y > height - 80) NewPage();
                canvas!.DrawText(section.Title, margin, y + 12, SKTextAlign.Left, head, ink); y += 18;
                var cols = Math.Max(1, section.Headers.Count);
                var colW = (width - 2 * margin) / cols;
                for (var c = 0; c < cols; c++) Cell(section.Headers[c], margin + c * colW, colW, body, true);
                y += rowHeight;
                foreach (var row in section.Rows.Take(60))
                {
                    if (y > height - 40) NewPage();
                    for (var c = 0; c < Math.Min(cols, row.Count); c++) Cell(row[c]?.ToString() ?? "", margin + c * colW, colW, body, false);
                    y += rowHeight;
                }
                y += 10;
            }
            if (canvas is not null) { canvas.DrawText($"{footer} · {page}", width - margin, height - margin / 2, SKTextAlign.Right, body, ink); document.EndPage(); }
        }
        return stream.ToArray();
    }
}
