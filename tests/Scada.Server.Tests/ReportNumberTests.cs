using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Scada.Core.Entities;
using Scada.Engine.Reports;
using Scada.Server.Reports;

namespace Scada.Server.Tests;

public class ReportNumberTests
{
    [Fact]
    public void PreviewExcelAndPdfShareOneDecimalText()
    {
        const double value = 1.225;
        var text = ReportNumbers.Format(value, 2);
        var table = new ReportTable("班报", "2026-02-01", ReportPeriod.Day, 0, 1,
            [new ReportColumnHeader("a", "压力", "MPa", 2, ReportColumnKind.TagAvg), new ReportColumnHeader("b", "计数", null, 0, ReportColumnKind.TagSum)],
            [new ReportRow("08:00", 0, 1, [value, 2])],
            new ReportRow("合计/汇总", 0, 1, [value, null]),
            0);

        using var book = new XLWorkbook(new MemoryStream(ReportExcel.Render(table, "v1", TimeZoneInfo.Utc)));
        var sheet = book.Worksheet(1);
        Assert.Equal(decimal.Parse(text, CultureInfo.InvariantCulture), sheet.Cell(5, 2).GetValue<decimal>());
        Assert.Equal(2m, sheet.Cell(5, 3).GetValue<decimal>());
        Assert.Equal(decimal.Parse(text, CultureInfo.InvariantCulture), sheet.Cell(6, 2).GetValue<decimal>());

        var node = JsonNode.Parse(JsonSerializer.Serialize(table, ReportTemplates.Json))!;
        ReportNumbers.ApplyDisplay(node);
        Assert.Equal(text, node["rows"]![0]!["values"]![0]!.GetValue<string>());
        Assert.Equal("2", node["rows"]![0]!["values"]![1]!.GetValue<string>());
        Assert.Equal(text, node["summary"]!["values"]![0]!.GetValue<string>());
        Assert.True(node["summary"]!["values"]![1] is null);

        var pdf = ReportPdf.Render(table, "v1", TimeZoneInfo.Utc, null);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf[..4]));
    }

    [Fact]
    public void IncompleteCoverageAppearsInPreviewExcelAndPdf()
    {
        var coverage = new ReportCoverage(1 / 24.0, true, 24, 1,
            [new ReportColumnCoverage("a", 1 / 24.0, true, 24, 1)],
            [new ReportRowCoverage("08:00", 0, 1, 1, false, 1, 1)]);
        var table = new ReportTable("班报", "2026-02-01", ReportPeriod.Day, 0, 1,
            [new ReportColumnHeader("a", "压力", "MPa", 2, ReportColumnKind.TagAvg)],
            [new ReportRow("08:00", 0, 1, [1.2])],
            new ReportRow("合计/汇总", 0, 1, [1.2]),
            0, coverage);

        var caption = ReportExcel.CoverageCaption(table);
        Assert.Contains("4.2%", caption);
        Assert.Contains("数据不完整", caption);

        using var book = new XLWorkbook(new MemoryStream(ReportExcel.Render(table, "v1", TimeZoneInfo.Utc)));
        Assert.Contains("4.2%", book.Worksheet(1).Cell(2, 1).GetString());
        Assert.Contains("数据不完整", book.Worksheet(1).Cell(2, 1).GetString());

        var node = JsonNode.Parse(JsonSerializer.Serialize(table, ReportTemplates.Json))!;
        Assert.True(node["coverage"]!["incomplete"]!.GetValue<bool>());
        Assert.Equal(1, node["coverage"]!["coveredHours"]!.GetValue<int>());

        var pdf = ReportPdf.Render(table, "v1", TimeZoneInfo.Utc, null);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf[..4]));
    }
}

