using System.Globalization;
using ClosedXML.Excel;
using Scada.Engine.Alarms;
using Scada.Server.Localization;
using SkiaSharp;

namespace Scada.Server.Reports;

// 报警分析导出：同一组“分节表格”渲染为 Excel（每节一个工作表）与 PDF（顺序排版）。文案以中文为源，按语言翻译。
public static class AlarmAnalyticsExport
{
    public sealed record Section(string Title, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<object?>> Rows);

    public static readonly IReadOnlyDictionary<string, string> KindLabels = new Dictionary<string, string>
    {
        ["chattering"] = "抖动", ["fleeting"] = "瞬时报警", ["standing"] = "长期驻留", ["frequent"] = "频繁报警", ["priorityReview"] = "优先级复核", ["consequential"] = "关联报警"
    };
    public static readonly IReadOnlyDictionary<string, string> KindAdvice = new Dictionary<string, string>
    {
        ["chattering"] = "加大恢复死区或设置恢复延时",
        ["fleeting"] = "设置触发延时",
        ["standing"] = "核实报警是否仍有效，考虑按工况抑制或调整设定值",
        ["frequent"] = "复核设定值与报警必要性",
        ["priorityReview"] = "复核优先级（高优先级占比超过目标）",
        ["consequential"] = "与关联报警同时出现，考虑按工况抑制或合并"
    };
    public static readonly IReadOnlyDictionary<string, string> FieldLabels = new Dictionary<string, string>
    {
        ["delaySec"] = "触发延时(秒)", ["offDelaySec"] = "恢复延时(秒)", ["recoverDeadband"] = "恢复死区", ["level"] = "等级",
        ["episodes"] = "抖动次数", ["activations"] = "触发次数", ["maxInWindow"] = "窗口内最多触发", ["windowSec"] = "窗口(秒)",
        ["count"] = "次数", ["ratio"] = "比例", ["p90DurationSec"] = "P90持续(秒)", ["hours"] = "小时", ["acked"] = "已确认",
        ["perDay"] = "每天", ["percent"] = "占比%", ["highPercent"] = "高优先级占比%", ["targetHighPercent"] = "目标%", ["together"] = "同现次数", ["ruleA"] = "关联规则"
    };
    private static readonly IReadOnlyDictionary<string, string> Ratings = new Dictionary<string, string>
    {
        ["acceptable"] = "可接受", ["manageable"] = "可管理", ["possiblyOverDemanding"] = "可能过高", ["likelyOverDemanding"] = "很可能过高", ["unacceptable"] = "不可接受"
    };
    private static readonly IReadOnlyDictionary<string, string> Levels = new Dictionary<string, string> { ["Info"] = "提示", ["Warning"] = "警告", ["Critical"] = "严重" };

    public static List<Section> Sections(AlarmAnalyticsReport report, TimeZoneInfo zone, string locale)
    {
        string L(string s) => Localizer.T(s, locale);
        string T(long ts) => ReportExcel.Local(ts, zone);
        string Lv(string level) => L(Levels.GetValueOrDefault(level, level));
        string Kv(IReadOnlyDictionary<string, double> d) => string.Join("; ", d.Select(x => $"{L(FieldLabels.GetValueOrDefault(x.Key, x.Key))}={x.Value.ToString("0.##", CultureInfo.InvariantCulture)}"));
        var k = report.Kpi; var th = report.Thresholds;
        var list = new List<Section>
        {
            new(L("概览"), [L("指标"), L("值"), L("参考")],
            [
                [L("统计区间"), $"{T(k.From)} ~ {T(k.To)}", null],
                [L("明细分析起点"), T(report.DetailFrom), report.UsedRollups ? L("更早部分使用预聚合") : null],
                [L("报警总数"), k.Total, null],
                [L("需报警数"), k.Annunciated, L("不含暂挂/抑制/抖动")],
                [L("操作员岗位数"), k.OperatorPositions, null],
                [L("每操作员每10分钟"), k.PerOperator10Min, $"≤{th.AcceptablePer10Min} / ≤{th.ManageablePer10Min}"],
                [L("每操作员每小时"), k.PerOperatorHour, "≤6 / ≤12"],
                [L("每操作员每天"), k.PerOperatorDay, "≤144 / ≤288"],
                [L("评级"), L(Ratings.GetValueOrDefault(k.Rating, k.Rating)), "EEMUA 191"],
                [L("峰值10分钟报警数"), k.Peak10Min, k.Peak10MinTs is long p ? T(p) : null],
                [L("洪水时间占比%"), k.PercentTimeInFlood, "<1%"],
                [L("洪水次数"), k.Floods.Count, $">{th.FloodStartPer10Min}/10min"],
                [L("平均确认时间(秒)"), k.AvgAckSec, null],
                [L("确认时间中位数(秒)"), k.MedianAckSec, null],
                [L("平均恢复时间(秒)"), k.AvgClearSec, null],
                [L("恢复时间中位数(秒)"), k.MedianClearSec, null],
                [L("长期驻留报警"), k.Standing.Count, $">{th.StandingHours}h"],
                [L("陈旧报警"), k.Stale.Count, $">{th.StaleHours}h"],
                [L("暂挂次数"), k.ShelveCount, null],
                [L("暂挂时长(小时)"), k.ShelvedHours, null],
                [L("抑制时长(小时)"), k.SuppressedHours, null],
                [L("瞬时报警数"), k.FleetingTotal, $"≤{th.FleetingSec}s"]
            ]),
            new(L("优先级分布"), [L("等级"), L("数量"), L("占比%"), L("目标%")],
                k.Priority.Select(p => (IReadOnlyList<object?>)[Lv(p.Level), p.Count, p.Percent, p.TargetPercent]).ToList()),
            new(L("最频繁报警"), [L("报警"), L("等级"), L("次数"), L("需报警"), L("抖动次数"), L("瞬时"), L("占比%"), L("累计%")],
                k.Top.Select(t => (IReadOnlyList<object?>)[t.Message, Lv(t.Level), t.Count, t.Annunciated, t.ChatterEpisodes, t.Fleeting, t.Percent, t.CumulativePercent]).ToList()),
            new(L("抖动报警"), [L("报警"), L("等级"), L("触发次数"), L("抖动次数"), L("窗口内最多触发")],
                k.ChatteringSources.Select(c => (IReadOnlyList<object?>)[c.Message, Lv(c.Level), c.Activations, c.Episodes, c.MaxInWindow]).ToList()),
            new(L("瞬时报警"), [L("报警"), L("等级"), L("瞬时次数"), L("总次数"), L("比例"), L("P90持续(秒)")],
                k.Fleeting.Select(f => (IReadOnlyList<object?>)[f.Message, Lv(f.Level), f.Count, f.Total, f.Ratio, f.P90DurationSec]).ToList()),
            new(L("关联报警"), [L("报警A"), L("次数A"), L("报警B"), L("次数B"), L("同现次数"), L("比例")],
                k.Consequential.Select(c => (IReadOnlyList<object?>)[c.MessageA, c.CountA, c.MessageB, c.CountB, c.Together, c.Ratio]).ToList()),
            new(L("报警洪水"), [L("开始"), L("结束"), L("报警数"), L("峰值10分钟"), L("首发报警"), L("主要报警")],
                k.Floods.Select(f => (IReadOnlyList<object?>)[T(f.StartTs), T(f.EndTs), f.Count, f.Peak10Min, f.FirstOut?.Message, string.Join("; ", f.Contributors.Take(3).Select(c => $"{c.Message}×{c.Count}"))]).ToList()),
            new(L("长期驻留报警"), [L("报警"), L("等级"), L("开始"), L("小时"), L("已确认")],
                k.Standing.Select(s => (IReadOnlyList<object?>)[s.Message, Lv(s.Level), T(s.StartTs), s.Hours, s.Acked ? L("是") : L("否")]).ToList()),
            new(L("合理化建议（仅供参考，不会自动修改）"), [L("类型"), L("报警"), L("建议"), L("依据"), L("当前值"), L("建议值")],
                report.Suggestions.Select(s => (IReadOnlyList<object?>)[L(KindLabels.GetValueOrDefault(s.Kind, s.Kind)), s.Message, L(KindAdvice.GetValueOrDefault(s.Kind, "")), Kv(s.Evidence), Kv(s.Current), Kv(s.Suggested)]).ToList())
        };
        return list;
    }

    public static byte[] Xlsx(string title, IReadOnlyList<Section> sections)
    {
        using var workbook = new XLWorkbook();
        var used = new HashSet<string>();
        foreach (var section in sections)
        {
            var name = new string(section.Title.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
            name = name.Length > 28 ? name[..28] : name;
            for (var i = 2; !used.Add(name); i++) name = $"{name[..Math.Min(name.Length, 25)]}_{i}";
            var sheet = workbook.Worksheets.Add(name);
            sheet.Cell(1, 1).Value = title; sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(2, 1).Value = section.Title;
            for (var c = 0; c < section.Headers.Count; c++) sheet.Cell(4, c + 1).Value = section.Headers[c];
            sheet.Row(4).Style.Font.Bold = true; sheet.Row(4).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
            var r = 5;
            foreach (var row in section.Rows)
            {
                for (var c = 0; c < row.Count; c++)
                {
                    var cell = sheet.Cell(r, c + 1);
                    cell.Value = row[c] switch
                    {
                        null => "—",
                        int i => i,
                        long l => l,
                        double d => d,
                        _ => row[c]!.ToString()
                    };
                }
                r++;
            }
            sheet.Columns().AdjustToContents(1, Math.Max(5, r), 8, 80);
            sheet.SheetView.FreezeRows(4);
        }
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
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
            using var text = new SKFont(typeface, 7.5f);
            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var grid = new SKPaint { Color = new SKColor(0xB0, 0xB8, 0xC4), StrokeWidth = 0.5f, Style = SKPaintStyle.Stroke };
            using var fill = new SKPaint { Color = new SKColor(0xE8, 0xEE, 0xF7), Style = SKPaintStyle.Fill };
            SKCanvas? canvas = null; float y = 0; var page = 0;
            void NewPage()
            {
                if (canvas is not null) { canvas.DrawText($"{footer} · {page}", width - margin, height - margin / 2, SKTextAlign.Right, text, ink); document.EndPage(); }
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
                if (y + 3 * rowHeight + 20 > height - margin) NewPage();
                canvas!.DrawText(section.Title, margin, y + 12, SKTextAlign.Left, head, ink); y += 18;
                var colWidth = (width - 2 * margin) / Math.Max(1, section.Headers.Count);
                void Header() { for (var c = 0; c < section.Headers.Count; c++) Cell(section.Headers[c], margin + c * colWidth, colWidth, text, true); y += rowHeight; }
                Header();
                foreach (var row in section.Rows.Take(200))
                {
                    if (y + rowHeight > height - margin) { NewPage(); Header(); }
                    for (var c = 0; c < row.Count; c++)
                        Cell(row[c] switch { null => "—", double d => d.ToString("0.###", CultureInfo.InvariantCulture), _ => row[c]!.ToString() ?? "" }, margin + c * colWidth, colWidth, text, false);
                    y += rowHeight;
                }
                if (section.Rows.Count == 0) { Cell("—", margin, width - 2 * margin, text, false); y += rowHeight; }
                y += 10;
            }
            canvas!.DrawText($"{footer} · {page}", width - margin, height - margin / 2, SKTextAlign.Right, text, ink);
            document.EndPage();
            document.Close();
        }
        return stream.ToArray();
    }
}
