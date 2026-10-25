using System.Globalization;
using ClosedXML.Excel;
using Scada.Core.Entities;
using Scada.Engine.Reports;
using SkiaSharp;

namespace Scada.Server.Reports;

public static class ReportExcel
{
    public static byte[] Render(ReportTable table, string versionLabel, TimeZoneInfo zone, string locale = "zh-CN")
    {
        string L(string text) => Scada.Server.Localization.Localizer.T(text, locale);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(L("报表"));
        sheet.Cell(1, 1).Value = table.Title; sheet.Cell(1, 1).Style.Font.Bold = true; sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Cell(2, 1).Value = L($"周期 {table.PeriodKey}（{Local(table.From, zone)} ～ {Local(table.To, zone)}） · {versionLabel}");
        var header = 4;
        sheet.Cell(header, 1).Value = L(table.Period == ReportPeriod.Month ? "日期" : "时段");
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var c = table.Columns[i];
            sheet.Cell(header, i + 2).Value = string.IsNullOrWhiteSpace(c.Unit) ? c.Title : $"{c.Title} ({c.Unit})";
        }
        sheet.Row(header).Style.Font.Bold = true; sheet.Row(header).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
        var row = header + 1;
        foreach (var r in table.Rows.Append(table.Summary))
        {
            sheet.Cell(row, 1).Value = L(r.Label);
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var cell = sheet.Cell(row, i + 2);
                if (r.Values[i] is double v) { cell.Value = Math.Round(v, table.Columns[i].Decimals); cell.Style.NumberFormat.Format = table.Columns[i].Decimals == 0 ? "0" : "0." + new string('0', table.Columns[i].Decimals); }
                else cell.Value = "—";
            }
            row++;
        }
        sheet.Row(row - 1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(header);
        sheet.Columns().AdjustToContents();
        // 归档文件保护：工作表加保护（无口令，防误改；不可修改性以服务端 SHA256 校验为准）。
        sheet.Protect();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    public static string Local(long ts, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ts), zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

// PDF：SkiaSharp（MIT，底层 Google Skia BSD-3）。直接绘制表格并嵌入字体子集，支持 TTC/OTF(CFF) 中文字体。
public static class ReportPdf
{
    private static readonly string[] Candidates =
    [
        @"C:\Windows\Fonts\msyh.ttc", @"C:\Windows\Fonts\simhei.ttf", @"C:\Windows\Fonts\simsun.ttc",
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc", "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc"
    ];

    public static SKTypeface ResolveTypeface(string? configured)
    {
        foreach (var path in new[] { configured }.Concat(Candidates))
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            // TTC 内含多语种字体，选择简体中文字形（Noto CJK 的 SC 序号为 2）。
            if (path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                for (var index = 0; index < 10; index++)
                {
                    var face = SKTypeface.FromFile(path, index);
                    if (face is null) break;
                    if (face.FamilyName.Contains(" SC", StringComparison.Ordinal) || !face.FamilyName.Contains("CJK", StringComparison.Ordinal)) return face;
                    face.Dispose();
                }
            var typeface = SKTypeface.FromFile(path);
            if (typeface is not null) return typeface;
        }
        return SKFontManager.Default.MatchCharacter('中') ?? SKTypeface.Default;
    }

    public static byte[] Render(ReportTable table, string versionLabel, TimeZoneInfo zone, string? fontPath, string locale = "zh-CN")
    {
        string L(string text) => Scada.Server.Localization.Localizer.T(text, locale);
        using var typeface = ResolveTypeface(fontPath);
        using var stream = new MemoryStream();
        var metadata = new SKDocumentPdfMetadata { Title = $"{table.Title} {table.PeriodKey}", Creator = "Scada", Producer = "Scada / SkiaSharp", Creation = DateTime.UtcNow };
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            const float width = 842, height = 595, margin = 32, rowHeight = 16;
            using var title = new SKFont(typeface, 14);
            using var text = new SKFont(typeface, 8);
            using var bold = new SKFont(typeface, 8) { Embolden = true };
            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var grid = new SKPaint { Color = new SKColor(0xB0, 0xB8, 0xC4), StrokeWidth = 0.5f, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { Color = new SKColor(0xE8, 0xEE, 0xF7), Style = SKPaintStyle.Fill };
            var labelWidth = 60f;
            var colWidth = Math.Max(40f, (width - 2 * margin - labelWidth) / Math.Max(1, table.Columns.Count));
            var columnsPerPage = Math.Max(1, (int)((width - 2 * margin - labelWidth) / colWidth));
            var all = table.Rows.Append(table.Summary).ToList();
            var rowsPerPage = (int)((height - 2 * margin - 60 - rowHeight) / rowHeight);
            var pages = new List<(int ColumnStart, int RowStart)>();
            for (var cs = 0; cs < table.Columns.Count; cs += columnsPerPage)
                for (var rs = 0; rs < all.Count; rs += rowsPerPage) pages.Add((cs, rs));
            var pageNo = 0;
            foreach (var (cs, rs) in pages)
            {
                pageNo++;
                var canvas = document.BeginPage(width, height);
                canvas.DrawText(table.Title, margin, margin + 12, SKTextAlign.Left, title, ink);
                canvas.DrawText(L($"周期 {table.PeriodKey}（{ReportExcel.Local(table.From, zone)} ～ {ReportExcel.Local(table.To, zone)}） · {versionLabel}"), margin, margin + 30, SKTextAlign.Left, text, ink);
                var y = margin + 46f;
                var cols = table.Columns.Skip(cs).Take(columnsPerPage).ToList();
                void Cell(string value, float x, float w, float top, SKFont font, bool right)
                {
                    canvas.DrawRect(x, top, w, rowHeight, grid);
                    var shown = value;
                    while (shown.Length > 1 && font.MeasureText(shown) > w - 4) shown = shown[..^1];
                    canvas.DrawText(shown, right ? x + w - 3 : x + 3, top + rowHeight - 4.5f, right ? SKTextAlign.Right : SKTextAlign.Left, font, ink);
                }
                canvas.DrawRect(margin, y, labelWidth + cols.Count * colWidth, rowHeight, fill);
                Cell(L(table.Period == ReportPeriod.Month ? "日期" : "时段"), margin, labelWidth, y, bold, false);
                for (var i = 0; i < cols.Count; i++)
                    Cell(string.IsNullOrWhiteSpace(cols[i].Unit) ? cols[i].Title : $"{cols[i].Title}({cols[i].Unit})", margin + labelWidth + i * colWidth, colWidth, y, bold, false);
                y += rowHeight;
                foreach (var row in all.Skip(rs).Take(rowsPerPage))
                {
                    var font = ReferenceEquals(row, table.Summary) ? bold : text;
                    Cell(L(row.Label), margin, labelWidth, y, font, false);
                    for (var i = 0; i < cols.Count; i++)
                    {
                        var value = row.Values[cs + i];
                        Cell(value is double v ? v.ToString("F" + cols[i].Decimals, CultureInfo.InvariantCulture) : "—", margin + labelWidth + i * colWidth, colWidth, y, font, true);
                    }
                    y += rowHeight;
                }
                canvas.DrawText(L($"第 {pageNo}/{pages.Count} 页 · 生成于 {ReportExcel.Local(table.GeneratedTs, zone)}"), width - margin, height - margin / 2, SKTextAlign.Right, text, ink);
                document.EndPage();
            }
            document.Close();
        }
        return stream.ToArray();
    }
}
