using Microsoft.EntityFrameworkCore;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using System.Text.Json;

namespace Scada.Server.Services;

/// <summary>
/// 对象模板草稿与发布的唯一落地点：REST 与 MCP 共用名称/草稿/发布校验、重名拒绝、乐观锁、
/// 版本追加、事务和成功审计。授权与 HTTP/MCP 返回形态仍由各入口负责。
/// </summary>
public enum ObjectTemplateCommandError
{
    None,
    InvalidName,
    InvalidDocument,
    DuplicateName,
    NotFound,
    Conflict
}

public sealed class ObjectTemplateCommandResult
{
    public bool Success { get; init; }
    public bool Created { get; init; }
    public ObjectTemplateCommandError Error { get; init; }
    public string? Message { get; init; }
    public DeviceObjectTemplate? Template { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    public static ObjectTemplateCommandResult Failed(ObjectTemplateCommandError error, string? message = null,
        DeviceObjectTemplate? template = null, IReadOnlyList<string>? errors = null) =>
        new() { Error = error, Message = message, Template = template, Errors = errors ?? [] };

    public static ObjectTemplateCommandResult Ok(DeviceObjectTemplate template, bool created = false) =>
        new() { Success = true, Created = created, Template = template };
}

public sealed class ObjectTemplateCommandService
{
    public const string InvalidNameMessage = "模板名称须为 1～100 个字符";
    public const string DuplicateNameMessage = "Template name already exists.";
    public const string ConflictMessage = "名称冲突或版本已变化，请重新打开并预览";
    public const string InvalidDraftMessage = "模板定义无效";
    public const string InvalidPublishMessage = "模板发布校验未通过";
    public const string NavigationTargetMessage = "面板导航目标不存在或未发布";

    public async Task<ObjectTemplateCommandResult> SaveAsync(
        ConfigDbContext db, RuntimeDbContext runtime, int? templateId, string? name, ObjectTemplateDocument document,
        int revision, string actorName, int? actorUserId, CancellationToken cancellationToken)
    {
        var text = name?.Trim() ?? "";
        if (text.Length is 0 or > 100)
            return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.InvalidName, InvalidNameMessage);
        var errors = ObjectTemplateValidator.Validate(document, false).ToArray();
        if (errors.Length > 0)
            return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.InvalidDocument, InvalidDraftMessage, errors: errors);

        var row = templateId is int id ? await db.ObjectTemplates.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) : null;
        if (templateId is not null && row is null) return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.NotFound, "Object template does not exist.");
        if (row is not null && row.Revision != revision)
            return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.Conflict, ConflictMessage, row);
        if (await db.ObjectTemplates.AnyAsync(x => x.Name == text && x.Id != (templateId ?? 0), cancellationToken))
            return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.DuplicateName, DuplicateNameMessage, row);

        var creating = row is null;
        var now = ScadaTime.NowMs();
        if (row is null)
        {
            row = new DeviceObjectTemplate { Revision = 1 };
            db.ObjectTemplates.Add(row);
        }
        else row.Revision++;
        row.Name = text;
        row.DraftJson = ObjectTemplateJson.Write(document);
        row.UpdatedTs = now;
        row.UpdatedBy = actorName;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.DuplicateName, DuplicateNameMessage, row);
        }
        var action = creating ? $"创建对象模板 {row.Name}" : $"保存对象模板 {row.Name} r{row.Revision}";
        WriteAudit(runtime, actorName, actorUserId, action, row.Id, now);
        await runtime.SaveChangesAsync(cancellationToken);
        return ObjectTemplateCommandResult.Ok(row, created: creating);
    }

    public async Task<ObjectTemplateCommandResult> PublishAsync(
        ConfigDbContext db, RuntimeDbContext runtime, int templateId, int revision,
        string actorName, int? actorUserId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.ObjectTemplates.SingleOrDefaultAsync(x => x.Id == templateId, cancellationToken);
        if (row is null) return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.NotFound, "Object template does not exist.");
        if (row.Revision != revision)
            return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.Conflict, ConflictMessage, row);

        ObjectTemplateDocument? document;
        try { document = ObjectTemplateJson.Read<ObjectTemplateDocument>(row.DraftJson); }
        catch (JsonException ex)
        {
            return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.InvalidDocument, "Stored draft cannot be parsed: " + ex.Message, row);
        }

        var errors = ObjectTemplateValidator.Validate(document, true).ToList();
        var targets = await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(cancellationToken);
        if (document?.Panel?.Nodes?.Any(n => n?.Action?.Type is "navigate" or "popup" && !targets.Contains(n.Action.ScreenId ?? 0)) == true)
            errors.Add(NavigationTargetMessage);
        if (errors.Count > 0)
            return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.InvalidDocument, InvalidPublishMessage, row, errors);

        var now = ScadaTime.NowMs();
        row.LatestVersion++;
        row.Revision++;
        row.UpdatedTs = now;
        row.UpdatedBy = actorName;
        db.ObjectTemplateVersions.Add(new DeviceObjectTemplateVersion
        {
            TemplateId = row.Id, Version = row.LatestVersion, DocumentJson = row.DraftJson,
            CreatedTs = now, CreatedBy = actorName
        });
        WriteAudit(runtime, actorName, actorUserId, $"发布对象模板 {row.Name} v{row.LatestVersion}", row.Id, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ObjectTemplateCommandResult.Failed(ObjectTemplateCommandError.Conflict, ConflictMessage, row);
        }
        return ObjectTemplateCommandResult.Ok(row);
    }

    private static void WriteAudit(RuntimeDbContext runtime, string actorName, int? actorUserId, string action, int templateId, long ts) =>
        runtime.OperationLogs.Add(new OperationLog
        {
            UserId = actorUserId, UserName = actorName, Ts = ts, Action = action,
            Result = "成功", TargetType = "DeviceObjectTemplate", TargetId = templateId == 0 ? null : templateId.ToString()
        });
}
