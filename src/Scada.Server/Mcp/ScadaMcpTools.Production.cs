using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Engine.Oee;
using Scada.Engine.Reports;
using Scada.Engine.Traceability;
using Scada.Server.Endpoints;

namespace Scada.Server.Mcp;

public sealed partial class ScadaMcpTools
{
    [McpServerTool(Name = "list_production_catalog", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Discover production line, product or shift IDs/names across all areas. kind is lines, products or shifts. Pages contain at most 100 records.")]
    public async Task<McpCursorPage<McpNamedItem>> ListProductionCatalog(CancellationToken cancellationToken,
        string kind = "lines", int afterId = 0, int limit = 50)
    {
        ValidatePage(afterId, limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = kind switch
        {
            "lines" => db.ProductionLines.AsNoTracking().Select(x => new { x.Id, x.Name, AreaId = (int?)x.AreaId }),
            "products" => db.ProductionProducts.AsNoTracking().Select(x => new { x.Id, x.Name, AreaId = (int?)x.AreaId }),
            "shifts" => db.ProductionShifts.AsNoTracking().Select(x => new { x.Id, x.Name, AreaId = (int?)x.AreaId }),
            _ => throw new McpException("kind must be lines, products or shifts.")
        };
        var rows = await query.Where(x => x.Id > afterId).OrderBy(x => x.Id).Take(limit + 1).ToListAsync(cancellationToken);
        return new(rows.Take(limit).Select(x => new McpNamedItem(x.Id, x.Name, x.AreaId)).ToArray(),
            rows.Count > limit ? rows[limit - 1].Id : null, ScadaTime.NowMs(), rows.Count > limit);
    }

    [McpServerTool(Name = "query_oee", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read OEE, availability/performance/quality, production counts, downtime Pareto and shift/product/day breakdowns for 1-10 lines over [from,to), at most 31 days. A/P/Q/OEE are ratios, not percentages; null means insufficient data. Each detail list is capped at 100 and ListTruncated is explicit.")]
    public Task<McpOeeResult> QueryOee(int[] lineIds, long from, long to, CancellationToken cancellationToken,
        int? productId = null, int? shiftId = null) =>
        queryBudget.RunAsync(async ct =>
        {
            var ids = ValidateLineIds(lineIds);
            ValidateWindow(from, to);
            await using var db = await factory.CreateDbContextAsync(ct);
            await RequireLinesAsync(db, ids, ct);
            if (productId is not null && (productId <= 0 || !await db.ProductionProducts.AnyAsync(x => x.Id == productId, ct)))
                throw new McpException("Product does not exist.");
            if (shiftId is not null && (shiftId <= 0 || !await db.ProductionShifts.AnyAsync(x => x.Id == shiftId, ct)))
                throw new McpException("Shift does not exist.");
            var thresholds = (await oee.GetThresholdsAsync(ct)).Thresholds.Clone();
            thresholds.TopN = Math.Min(thresholds.TopN, 20);
            var result = await oee.AnalyzeAsync(from, to, ids, productId, shiftId, ScadaTime.NowMs(), thresholds, ct);
            var kpi = result.Kpi;
            var truncated = kpi.Faults.Count > 100 || kpi.ByShift.Count > 100 || kpi.ByProduct.Count > 100
                || kpi.ByDay.Count > 100 || result.Trend.Count > 100;
            return new McpOeeResult(result with
            {
                Kpi = kpi with
                {
                    Faults = kpi.Faults.Take(100).ToArray(), ByShift = kpi.ByShift.Take(100).ToArray(),
                    ByProduct = kpi.ByProduct.Take(100).ToArray(), ByDay = kpi.ByDay.Take(100).ToArray()
                },
                Trend = result.Trend.Take(100).ToArray()
            }, truncated);
        }, cancellationToken);

    [McpServerTool(Name = "query_downtimes", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read downtime events overlapping [from,to), at most 31 days and 1-10 production lines. Optional confirmed filter; no confirmation or classification writes. Descending ID cursor, at most 100 rows.")]
    public async Task<McpRecordPage<McpDowntime>> QueryDowntimes(int[] lineIds, long from, long to,
        CancellationToken cancellationToken, bool? confirmed = null, long? beforeId = null, int limit = 50)
    {
        var ids = ValidateLineIds(lineIds);
        ValidateWindow(from, to);
        ValidateCursor(beforeId, limit);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await RequireLinesAsync(db, ids, cancellationToken);
        var query = db.DowntimeEvents.AsNoTracking().Where(x => ids.Contains(x.LineId) && x.StartTs < to && (x.EndTs == null || x.EndTs > from));
        if (confirmed is not null) query = query.Where(x => x.Confirmed == confirmed);
        if (beforeId is not null) query = query.Where(x => x.Id < beforeId);
        var rows = await query.OrderByDescending(x => x.Id).Take(limit + 1)
            .Select(x => new McpDowntime(x.Id, x.LineId, x.StartTs, x.EndTs, x.ReasonId, x.State, x.Source, x.Note, x.Confirmed))
            .ToListAsync(cancellationToken);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }

    [McpServerTool(Name = "query_traceability", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read persisted product captures over an inclusive time range of at most 31 days. Optional exact SN, job ID and inspection outcome; outcome is independent of PLC acknowledgement. Descending ID cursor, 1-50 records. No scan binding or PLC acknowledgement.")]
    public async Task<McpRecordPage<TraceabilityRecordResponse>> QueryTraceability(long from, long to,
        CancellationToken cancellationToken, string? sn = null, int? jobId = null,
        TraceabilityOutcome? outcome = null, long? beforeId = null, int limit = 20)
    {
        ValidateWindow(from, to);
        ValidateCursor(beforeId, limit);
        if (limit > 50 || sn?.Length > 200 || jobId is <= 0 || outcome is not null && !Enum.IsDefined(outcome.Value))
            throw new McpException("Traceability requires limit 1-50, SN up to 200 characters, positive job ID and a valid outcome.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.TraceabilityRecords.AsNoTracking().Where(x => x.Ts >= from && x.Ts <= to);
        if (!string.IsNullOrWhiteSpace(sn)) query = query.Where(x => x.Sn == sn.Trim());
        if (jobId is not null) query = query.Where(x => x.JobId == jobId);
        if (outcome is not null) query = query.Where(x => x.Outcome == outcome);
        if (beforeId is not null) query = query.Where(x => x.Id < beforeId);
        var rows = await query.OrderByDescending(x => x.Id).Take(limit + 1)
            .Select(x => new { x.Id, x.JobId, x.JobName, x.Sn, x.Ts, x.Acknowledged, x.Outcome,
                Values = x.ValuesJson.Length <= 65536 ? x.ValuesJson : null }).ToListAsync(cancellationToken);
        var items = rows.Take(limit).Select(x =>
        {
            var values = ReadStoredJson<List<TraceabilityValue>>(x.Values, "Traceability values");
            if (values.Count > 100 || values.Any(value => value is null || !double.IsFinite(value.Value)))
                throw new McpException("Traceability values exceed 100 fields or contain invalid data.");
            return new TraceabilityRecordResponse(x.Id, x.JobId, x.JobName, x.Sn,
                DateTimeOffset.FromUnixTimeMilliseconds(x.Ts), x.Acknowledged, x.Outcome, values);
        }).ToArray();
        return new(items, rows.Count > limit ? rows[limit - 1].Id : null);
    }

    [McpServerTool(Name = "query_report", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read daily (hourly rows, at most 24 hours) or monthly (daily rows, at most 31 days) statistics for 1-10 tags from STORED hourly aggregates. Does not backfill recent raw samples, generate files or send email. Missing aggregates are not zero.")]
    public Task<McpStoredReport> QueryReport(int[] tagIds, long from, long to, CancellationToken cancellationToken,
        string type = "daily") =>
        queryBudget.RunAsync(async ct =>
        {
            if (type is not ("daily" or "monthly")) throw new McpException("type must be daily or monthly.");
            ValidateWindow(from, to, type == "daily" ? 1 : 31);
            var ids = ValidateIds(tagIds, 10);
            await RequireTagsAsync(ids, ct);
            var result = await reports.BuildStoredAsync(type, ids, from, to, ct);
            return new McpStoredReport(result, "stored_hourly_aggregates", false, ids.Except(result.Series.Select(x => x.TagId)).ToArray());
        }, cancellationToken);

    [McpServerTool(Name = "list_report_runs", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List existing archived report metadata generated in a range of at most 31 days. Optional template ID; descending run ID cursor, at most 100 rows. Excludes filesystem paths, signatures and email settings. Does not generate a report.")]
    public async Task<McpRecordPage<McpReportRun>> ListReportRuns(long from, long to, CancellationToken cancellationToken,
        int? templateId = null, long? beforeId = null, int limit = 50)
    {
        ValidateWindow(from, to);
        ValidateCursor(beforeId, limit);
        if (templateId is <= 0) throw new McpException("templateId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.ReportRuns.AsNoTracking().Where(x => x.GeneratedTs >= from && x.GeneratedTs <= to);
        if (templateId is not null) query = query.Where(x => x.TemplateId == templateId);
        if (beforeId is not null) query = query.Where(x => x.Id < beforeId);
        var rows = await query.OrderByDescending(x => x.Id).Take(limit + 1)
            .Select(x => new McpReportRun(x.Id, x.TemplateId, x.TemplateName, x.AreaId, x.PeriodKey,
                x.FromTs, x.ToTs, x.Version, x.GeneratedTs, x.PurgedTs)).ToListAsync(cancellationToken);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }

    [McpServerTool(Name = "read_report_run", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read a paged table from an existing report's stored data snapshot, not its PDF/XLSX file. At most 100 rows per page and 100 columns; oversized or malformed snapshots return an error. No regeneration, signing or approvals.")]
    public async Task<McpReportTablePage> ReadReportRun(long runId, CancellationToken cancellationToken,
        int offset = 0, int limit = 50)
    {
        ValidatePage(offset, limit);
        if (runId <= 0 || offset > 10000) throw new McpException("runId must be positive and offset must not exceed 10000.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.ReportRuns.AsNoTracking().Where(x => x.Id == runId)
            .Select(x => new { Data = x.DataJson.Length <= 2 * 1024 * 1024 ? x.DataJson : null })
            .SingleOrDefaultAsync(cancellationToken) ?? throw new McpException("Report run does not exist.");
        var table = ReadStoredJson<ReportTable>(row.Data, "Report snapshot");
        if (table.Columns is null || table.Rows is null || table.Summary is null || table.Columns.Count > 100
            || table.Rows.Count > 10000 || table.Rows.Any(x => x is null || x.Values is null || x.Values.Length != table.Columns.Count)
            || table.Summary.Values is null || table.Summary.Values.Length != table.Columns.Count)
            throw new McpException("Report snapshot has an invalid table or exceeds 100 columns / 10000 rows.");
        return new(runId, table.Title, table.PeriodKey, table.Period, table.From, table.To, table.GeneratedTs,
            table.Columns, table.Rows.Skip(offset).Take(limit).ToArray(), table.Summary, table.Rows.Count,
            (long)offset + limit < table.Rows.Count ? offset + limit : null);
    }

    private static T ReadStoredJson<T>(string? json, string label)
    {
        if (json is null) throw new McpException($"{label} exceeds the MCP size limit.");
        try { return JsonSerializer.Deserialize<T>(json, McpEndpoints.Json) ?? throw new McpException($"{label} is null."); }
        catch (JsonException ex) { throw new McpException($"{label} is not valid JSON for the expected schema.", ex); }
    }

    private static int[] ValidateLineIds(int[]? ids)
    {
        if (ids is null || ids.Length is < 1 or > 10 || ids.Any(id => id <= 0))
            throw new McpException("lineIds must contain 1-10 positive IDs.");
        return ids.Distinct().ToArray();
    }

    private static async Task RequireLinesAsync(Scada.Data.ConfigDbContext db, int[] ids, CancellationToken ct)
    {
        if (await db.ProductionLines.CountAsync(x => ids.Contains(x.Id), ct) != ids.Length)
            throw new McpException("One or more production lines do not exist.");
    }

    private static void ValidateCursor(long? beforeId, int limit)
    {
        ValidatePage(0, limit);
        if (beforeId is <= 0) throw new McpException("beforeId must be positive.");
    }

    public sealed record McpNamedItem(int Id, string Name, int? AreaId);
    public sealed record McpRecordPage<T>(IReadOnlyList<T> Items, long? NextBeforeId);
    public sealed record McpOeeResult(OeeReport Report, bool ListsTruncated);
    public sealed record McpDowntime(long Id, int LineId, long StartTs, long? EndTs, int? ReasonId, LineState State, string Source, string? Note, bool Confirmed);
    public sealed record McpStoredReport(ReportResult Report, string Source, bool RecentRawIncluded, int[] MissingTagIds);
    public sealed record McpReportRun(long Id, int TemplateId, string TemplateName, int AreaId, string PeriodKey,
        long FromTs, long ToTs, int Version, long GeneratedTs, long? PurgedTs);
    public sealed record McpReportTablePage(long RunId, string Title, string PeriodKey, ReportPeriod Period, long From, long To,
        long GeneratedTs, IReadOnlyList<ReportColumnHeader> Columns, IReadOnlyList<ReportRow> Rows, ReportRow Summary, int TotalRows, int? NextOffset);
}
