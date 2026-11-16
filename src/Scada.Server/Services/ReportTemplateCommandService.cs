using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Reports;

namespace Scada.Server.Services;

/// <summary>
/// 报表模板保存的唯一落地点：REST 与 MCP 共用周期/列/引用/邮件通道校验、重名拒绝、修订冲突、
/// 首次启用调度的 ScheduleSinceTs、事务和成功审计。区域权限与测点/任务可见性仍由各入口负责。
/// </summary>
public enum ReportTemplateCommandError
{
    None,
    Invalid,
    AreaMissing,
    TagsMissing,
    JobMissing,
    EmailChannelInvalid,
    DuplicateName,
    NotFound,
    Conflict
}

public sealed class ReportTemplateCommandResult
{
    public bool Success { get; init; }
    public bool Created { get; init; }
    public ReportTemplateCommandError Error { get; init; }
    public string? Message { get; init; }
    public ReportTemplate? Template { get; init; }
    public int[] MissingTagIds { get; init; } = [];
    public int? MissingJobId { get; init; }

    public static ReportTemplateCommandResult Failed(ReportTemplateCommandError error, string? message = null,
        ReportTemplate? template = null, int[]? missingTags = null, int? missingJobId = null) =>
        new() { Error = error, Message = message, Template = template, MissingTagIds = missingTags ?? [], MissingJobId = missingJobId };

    public static ReportTemplateCommandResult Ok(ReportTemplate template, bool created) =>
        new() { Success = true, Created = created, Template = template };
}

public sealed class ReportTemplateCommandService
{
    public const string AreaMissingMessage = "区域不存在";
    public const string EmailChannelMessage = "报表投递须选择邮件通道";
    public const string DuplicateNameMessage = "模板名称已存在";
    public const string ConflictMessage = "模板已被修改，请重新读取后核对";

    public async Task<ReportTemplateCommandResult> SaveAsync(
        ConfigDbContext db, RuntimeDbContext runtime, int? templateId, string? name, ReportPeriod period, int areaId,
        string? shiftStarts, IReadOnlyList<ReportColumn> columns, bool scheduleEnabled, int? scheduleDelayMin,
        int? emailChannelId, string? emailRecipients, long revision, string actorName, int? actorUserId,
        CancellationToken cancellationToken)
    {
        columns ??= [];
        var candidate = new ReportTemplate
        {
            Name = name?.Trim() ?? "",
            Period = period,
            AreaId = areaId,
            ShiftStarts = string.IsNullOrWhiteSpace(shiftStarts) ? "08:00,20:00" : shiftStarts.Trim(),
            ColumnsJson = JsonSerializer.Serialize(columns, ReportTemplates.Json),
            ScheduleEnabled = scheduleEnabled,
            ScheduleDelayMin = scheduleDelayMin ?? ReportTemplate.DefaultScheduleDelayMin,
            EmailChannelId = emailChannelId,
            EmailRecipients = emailRecipients?.Trim() ?? ""
        };
        try { ReportTemplates.Validate(candidate, columns); }
        catch (ReportTemplateException ex)
        {
            return ReportTemplateCommandResult.Failed(ReportTemplateCommandError.Invalid, ex.Message);
        }

        if (!await db.Areas.AnyAsync(x => x.Id == areaId, cancellationToken))
            return ReportTemplateCommandResult.Failed(ReportTemplateCommandError.AreaMissing, AreaMissingMessage);

        var tagIds = columns.Where(c => c.Kind <= ReportColumnKind.TagSum && c.TagId is int).Select(c => c.TagId!.Value).Distinct().ToArray();
        var missingTags = tagIds.Except(await db.Tags.Where(x => tagIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken)).ToArray();
        if (missingTags.Length > 0)
            return ReportTemplateCommandResult.Failed(ReportTemplateCommandError.TagsMissing, $"测点不存在：{string.Join(",", missingTags)}", missingTags: missingTags);

        foreach (var column in columns.Where(c => c.Kind == ReportColumnKind.CalcSnapshot))
        {
            if (column.JobId is not int jobId || !await db.CalcJobs.AnyAsync(x => x.Id == jobId, cancellationToken))
                return ReportTemplateCommandResult.Failed(ReportTemplateCommandError.JobMissing, $"计算任务 #{column.JobId} 不存在", missingJobId: column.JobId);
        }

        if (candidate.EmailChannelId is int channelId)
        {
            var channel = await db.NotificationChannels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == channelId, cancellationToken);
            if (channel is null || channel.Type != NotificationChannelType.Email)
                return ReportTemplateCommandResult.Failed(ReportTemplateCommandError.EmailChannelInvalid, EmailChannelMessage);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = templateId is int id ? await db.ReportTemplates.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) : null;
        if (templateId is not null && row is null)
            return ReportTemplateCommandResult.Failed(ReportTemplateCommandError.NotFound, "Report template does not exist.");
        if (row is not null && row.Revision != revision)
            return ReportTemplateCommandResult.Failed(ReportTemplateCommandError.Conflict, ConflictMessage, row);
        if (await db.ReportTemplates.AnyAsync(x => x.Name == candidate.Name && x.Id != (templateId ?? 0), cancellationToken))
            return ReportTemplateCommandResult.Failed(ReportTemplateCommandError.DuplicateName, DuplicateNameMessage, row);

        var creating = row is null;
        var now = ScadaTime.NowMs();
        var wasScheduled = row?.ScheduleEnabled ?? false;
        if (row is null)
        {
            row = new ReportTemplate { Revision = 1 };
            db.ReportTemplates.Add(row);
        }
        else row.Revision++;
        row.Name = candidate.Name;
        row.Period = candidate.Period;
        row.AreaId = candidate.AreaId;
        row.ShiftStarts = candidate.ShiftStarts;
        row.ColumnsJson = candidate.ColumnsJson;
        row.ScheduleEnabled = candidate.ScheduleEnabled;
        row.ScheduleDelayMin = candidate.ScheduleDelayMin;
        if (candidate.ScheduleEnabled && !wasScheduled) row.ScheduleSinceTs = now;
        row.EmailChannelId = candidate.EmailChannelId;
        row.EmailRecipients = candidate.EmailRecipients;
        row.UpdatedTs = now;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            var action = creating ? $"创建报表模板：{row.Name}" : $"修改报表模板：{row.Name}";
            runtime.OperationLogs.Add(new OperationLog
            {
                UserId = actorUserId, UserName = actorName, Ts = now, Action = action,
                Result = "成功", TargetType = "ReportTemplate", TargetId = row.Id.ToString()
            });
            await transaction.CommitAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ReportTemplateCommandResult.Failed(ReportTemplateCommandError.DuplicateName, DuplicateNameMessage, row);
        }
        return ReportTemplateCommandResult.Ok(row, created: creating);
    }
}
