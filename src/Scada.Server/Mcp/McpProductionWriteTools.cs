using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Oee;
using Scada.Engine.Reports;
using Scada.Server.Endpoints;
using Scada.Server.Services;
using Scada.Server.Reports;
using static Scada.Server.Mcp.McpActors;
using static Scada.Server.Mcp.McpAuditLog;
using static Scada.Server.Mcp.McpText;

namespace Scada.Server.Mcp;

// 生产与报表写工具：停机候选确认/归类、报表模板维护与手动生成。
// 模板维护复用网页端的全部结构与引用校验；生成复用 ReportArchiveService（XLSX/PDF、SHA256、版本追加、审计）。
[McpServerToolType]
public sealed class McpProductionWriteTools(
    IDbContextFactory<ConfigDbContext> factory,
    IDbContextFactory<RuntimeDbContext> runtimeFactory,
    ReportArchiveService reportArchive,
    OeeRollupService rollups,
    DowntimeCommandService downtimes,
    ReportTemplateCommandService reports,
    McpIdempotency idempotency)
{
    [McpServerTool(Name = "confirm_downtime", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Confirm one downtime event as mcp:lan and classify it with a reasonId when the event is not yet classified; an optional note is at most 500 characters. Confirmation triggers an OEE rollup rebuild from the event start. Repeating the same confirmation is a no-op. success=false when the event or line does not exist.")]
    public async Task<McpDowntimeConfirmed> ConfirmDowntime(long downtimeId, CancellationToken cancellationToken,
        [Description("Downtime reason ID required to confirm an unclassified event; may be omitted when the event already has a reason.")] int? reasonId = null,
        [Description("Optional note, at most 500 characters.")] string? note = null)
    {
        if (downtimeId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "downtimeId must be positive.");
        if (reasonId is int reason && reason <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "reasonId must be positive.");
        var text = OptionalText(note, 500, "note");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        var result = await downtimes.ConfirmAsync(db, runtime, rollups, downtimeId, reasonId, text, text is not null, McpActorName,
            (row, line, already, previousReason, previousNote, now) =>
            {
                runtime.OperationLogs.Add(Audit($"确认停机 #{row.Id}：{(reasonId is int r ? $"原因 #{r}" : "保持原因")}{(text is null ? "" : $"；{text}")}",
                    "DowntimeEvent", (int)row.Id, "成功",
                    $"confirmed={already};reasonId={previousReason}",
                    $"confirmed=True;reasonId={row.ReasonId}", now));
            },
            cancellationToken);
        if (result.Error is DowntimeCommandError.NotFound)
            throw new McpToolException(McpErrorCodes.NotFound, "Downtime event does not exist.");
        if (result.Error is DowntimeCommandError.ReasonRequired)
            throw new McpToolException(McpErrorCodes.InvalidArgument, result.Message ?? "A downtime reason is required to confirm the event.");
        if (result.Error is DowntimeCommandError.ReasonMissing)
            throw new McpToolException(McpErrorCodes.NotFound, "Downtime reason does not exist.");
        var row = result.Event!;
        var now = ScadaTime.NowMs();
        if (result.Error is DowntimeCommandError.LineMissing)
            return new(row.Id, false, result.Message ?? "The production line of this downtime event no longer exists.", row.LineId, null,
                row.Confirmed, row.ReasonId, row.State.ToString(), row.StartTs, row.EndTs, row.Note, null, now);
        var line = result.Line!;
        return new(row.Id, result.Success, result.Message ?? "Downtime event confirmed.", row.LineId, line.Name,
            row.Confirmed, row.ReasonId, row.State.ToString(), row.StartTs, row.EndTs, row.Note,
            result.Unchanged ? null : row.StartTs, now);
    }

    [McpServerTool(Name = "save_report_template", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create or replace one report template as mcp:lan. Pass templateId and its revision to update; omit both to create. Validates the period, shift starts, columns and formulas, referenced tags and calculation jobs, the email channel and unique names. Schedule changes apply to future periods only. success=false on revision conflicts or validation errors.")]
    public async Task<McpReportTemplateSaved> SaveReportTemplate(string name, ReportPeriod period, int areaId, ReportColumn[] columns,
        CancellationToken cancellationToken,
        [Description("Existing template ID; omit to create a new template.")] int? templateId = null,
        [Description("Current revision of the template being updated; ignored when creating.")] long revision = 0,
        [Description("Shift start times such as 08:00,20:00; required format when period is Shift.")] string? shiftStarts = null,
        [Description("Generate this template on schedule.")] bool scheduleEnabled = false,
        [Description("Minutes to wait after the period ends before generating, 0 to 1440; default 10.")] int? scheduleDelayMin = null,
        [Description("Optional email channel ID used to deliver generated reports.")] int? emailChannelId = null,
        [Description("Semicolon/comma separated email recipients, at most 1000 characters.")] string? emailRecipients = null)
    {
        var text = RequiredText(name, 100, "name");
        if (!Enum.IsDefined(period)) throw new McpToolException(McpErrorCodes.InvalidArgument, "period is invalid.");
        if (areaId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "areaId must be positive.");
        if (templateId is int id && id <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "templateId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        if (scheduleDelayMin is int delay && delay is < 0 or > 1440)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "scheduleDelayMin must be between 0 and 1440.");
        if (emailChannelId is int channelKey && channelKey <= 0)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "emailChannelId must be positive.");
        columns ??= [];
        if (columns.Any(x => x is null)) throw new McpToolException(McpErrorCodes.InvalidArgument, "columns must not contain null entries.");
        var shifts = OptionalText(shiftStarts, 100, "shiftStarts");
        var recipients = OptionalText(emailRecipients, 1000, "emailRecipients");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        var result = await reports.SaveAsync(db, runtime, templateId, text, period, areaId, shifts, columns, scheduleEnabled, scheduleDelayMin,
            emailChannelId, recipients, revision, McpActorName, null, cancellationToken);
        var now = ScadaTime.NowMs();
        if (result.Error is ReportTemplateCommandError.NotFound) throw new McpToolException(McpErrorCodes.NotFound, "Report template does not exist.");
        if (result.Error is ReportTemplateCommandError.AreaMissing) throw new McpToolException(McpErrorCodes.NotFound, "Area does not exist.");
        var row = result.Template;
        if (result.Error is ReportTemplateCommandError.Conflict)
            return new(row!.Id, false, $"Template was changed by another editor (current revision {row.Revision}); reload and retry.", row.Revision, row.ScheduleSinceTs, now);
        if (result.Error is ReportTemplateCommandError.DuplicateName)
            return new(row?.Id ?? 0, false, "Template name already exists.", row?.Revision ?? 0, row?.ScheduleSinceTs, now);
        if (result.Error is ReportTemplateCommandError.TagsMissing)
            return new(templateId ?? 0, false, $"Referenced tags do not exist: {string.Join(",", result.MissingTagIds)}", 0, null, now);
        if (result.Error is ReportTemplateCommandError.JobMissing)
            return new(templateId ?? 0, false, $"Calculation job {result.MissingJobId} does not exist.", 0, null, now);
        if (result.Error is ReportTemplateCommandError.EmailChannelInvalid)
            return new(templateId ?? 0, false, "Reports can only be delivered through an email channel.", 0, null, now);
        if (!result.Success)
            return new(templateId ?? 0, false, result.Message ?? "Rejected.", row?.Revision ?? 0, row?.ScheduleSinceTs, now);
        return new(row!.Id, true, result.Created ? "Report template created." : "Report template saved.", row.Revision, row.ScheduleSinceTs, ScadaTime.NowMs());
    }

        [McpServerTool(Name = "generate_report", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Generate one report from an existing template as mcp:lan, appending a new immutable version in the archive with XLSX/PDF hashes and audit. Omit at to generate the previous complete period, or pass a Unix-millisecond timestamp inside the required period. Returns run metadata only, never file paths. success=false when the template or stored columns are invalid.")]
    public Task<McpReportGenerated> GenerateReport(int templateId, CancellationToken cancellationToken,
        [Description("Unix milliseconds inside the period to generate; omit for the previous complete period.")] long? at = null,
        [Description(McpIdempotency.Hint)] Guid? requestId = null) =>
        idempotency.RunAsync("generate_report", requestId, cancellationToken, async () =>
        {
            if (templateId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "templateId must be positive.");
            if (at is long anchor && anchor < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "at must be non-negative Unix milliseconds.");
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var template = await db.ReportTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == templateId, cancellationToken)
                ?? throw new McpToolException(McpErrorCodes.NotFound, "Report template does not exist.");
            var periodAt = at ?? ReportPeriods.Previous(template.Period, template.ShiftStarts, ScadaTime.NowMs(), reportArchive.Zone).From;
            try
            {
                var run = await reportArchive.GenerateAsync(template, periodAt, "manual", null, McpActorName, cancellationToken);
                return new McpReportGenerated(templateId, run.TemplateName, run.TemplateRevision, true, "Report generated.",
                    run.Id, run.PeriodKey, run.FromTs, run.ToTs, run.Version, run.Trigger, run.GeneratedTs,
                    run.XlsxSha256, run.PdfSha256, run.XlsxSignature is not null && run.PdfSignature is not null, ScadaTime.NowMs());
            }
            catch (ReportTemplateException ex)
            {
                return new McpReportGenerated(templateId, template.Name, template.Revision, false, ex.Message, null, "", null, null, null, null, null, null, null, false, ScadaTime.NowMs());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new McpReportGenerated(templateId, template.Name, template.Revision, false, "Report archive write failed: " + ex.Message,
                    null, "", null, null, null, null, null, null, null, false, ScadaTime.NowMs());
            }
        });

    public sealed record McpDowntimeConfirmed(long DowntimeId, bool Success, string Message, int LineId, string? LineName,
        bool Confirmed, int? ReasonId, string State, long StartTs, long? EndTs, string? Note, long? RollupFromTs, long DataAsOf);
    public sealed record McpReportTemplateSaved(int TemplateId, bool Success, string Message, long Revision, long? ScheduleSinceTs, long DataAsOf);
    public sealed record McpReportGenerated(int TemplateId, string TemplateName, long TemplateRevision, bool Success, string Message,
        long? RunId, string PeriodKey, long? FromTs, long? ToTs, int? Version, string? Trigger, long? GeneratedTs,
        string? XlsxSha256, string? PdfSha256, bool Signed, long DataAsOf);
}
