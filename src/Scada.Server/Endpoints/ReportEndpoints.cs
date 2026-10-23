using ClosedXML.Excel;
using Scada.Core.Interfaces;
using Scada.Engine.Reports;

namespace Scada.Server.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports");

        group.MapGet("", async (
            string? type,
            string? from,
            string? to,
            string? tagIds,
            ReportService reportService,
            CancellationToken cancellationToken) =>
        {
            var error = ResolveRange(type, from, to, out var reportType, out var fromMs, out var toMs);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            var ids = EndpointHelpers.ParseIds(tagIds);
            if (ids.Length == 0)
            {
                return EndpointHelpers.BadRequest("请指定 tagIds");
            }

            var report = await reportService.BuildAsync(reportType, ids, fromMs, toMs, cancellationToken);
            return Results.Ok(report);
        });

        group.MapGet("/export", (
            string? type,
            string? from,
            string? to,
            string? tagIds,
            ReportService reportService,
            IConfigSnapshot config,
            CancellationToken cancellationToken) =>
        {
            var error = ResolveRange(type, from, to, out var reportType, out var fromMs, out var toMs);
            if (error is not null)
            {
                return Task.FromResult(EndpointHelpers.BadRequest(error));
            }

            var ids = EndpointHelpers.ParseIds(tagIds);
            if (ids.Length == 0)
            {
                return Task.FromResult(EndpointHelpers.BadRequest("请指定 tagIds"));
            }

            return ExportAsync(reportService, config, reportType, ids, fromMs, toMs, cancellationToken);
        });
    }

    private static async Task<IResult> ExportAsync(
        ReportService reportService,
        IConfigSnapshot config,
        string type,
        int[] ids,
        long from,
        long to,
        CancellationToken cancellationToken)
    {
        var report = await reportService.BuildAsync(type, ids, from, to, cancellationToken);
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("报表");
        var headers = new[] { "时间", "测点", "平均值", "最小值", "最大值", "样本数" };
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        var row = 2;
        foreach (var series in report.Series)
        {
            var name = config.TryGetTag(series.TagId, out var tag) ? tag.Name : $"#{series.TagId}";
            foreach (var point in series.Points)
            {
                sheet.Cell(row, 1).Value = DateTimeOffset.FromUnixTimeMilliseconds(point.Ts).ToLocalTime().DateTime;
                sheet.Cell(row, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                sheet.Cell(row, 2).Value = name;
                sheet.Cell(row, 3).Value = point.Avg;
                sheet.Cell(row, 4).Value = point.Min;
                sheet.Cell(row, 5).Value = point.Max;
                sheet.Cell(row, 6).Value = point.Count;
                row++;
            }
        }

        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return Results.File(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"report_{type}_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    private static string? ResolveRange(string? type, string? from, string? to, out string reportType, out long fromMs, out long toMs)
    {
        reportType = string.Equals(type, "monthly", StringComparison.OrdinalIgnoreCase) ? "monthly" : "daily";
        var now = DateTimeOffset.UtcNow;
        var defaultFrom = reportType == "monthly"
            ? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var defaultTo = reportType == "monthly" ? defaultFrom.AddMonths(1) : defaultFrom.AddDays(1);

        fromMs = EndpointHelpers.ParseTime(from, defaultFrom.ToUnixTimeMilliseconds());
        toMs = EndpointHelpers.ParseTime(to, defaultTo.ToUnixTimeMilliseconds());
        if (toMs <= fromMs)
        {
            return "结束时间必须晚于开始时间";
        }

        return null;
    }
}
