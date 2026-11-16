using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Projects;
using Scada.Server.Screens;

namespace Scada.Server.Services;

/// <summary>
/// 画面草稿与发布的唯一落地点：REST 与 MCP 共用名称/区域/交叉区域测点/结构校验、
/// 乐观锁、发布冻结与历史追加。授权与返回形态仍由各入口负责。
/// </summary>
public enum ScreenCommandError
{
    None,
    InvalidName,
    InvalidNote,
    InvalidDocument,
    EmptyDocument,
    Unreadable,
    AreaMissing,
    AreaMismatch,
    NotFound,
    VersionNotFound,
    Conflict,
    ValidationFailed
}

public sealed class ScreenCommandResult
{
    public bool Success { get; init; }
    public ScreenCommandError Error { get; init; }
    public string? Message { get; init; }
    public ProcessScreen? Screen { get; init; }
    public IReadOnlyList<ScreenIssue> Issues { get; init; } = [];
    public IReadOnlyList<ScreenIssue> Warnings { get; init; } = [];
    public int? ConfigVersionId { get; init; }
    public bool SnapshotFailed { get; init; }

    public static ScreenCommandResult Failed(ScreenCommandError error, string? message = null,
        IReadOnlyList<ScreenIssue>? issues = null, IReadOnlyList<ScreenIssue>? warnings = null, ProcessScreen? screen = null) =>
        new() { Error = error, Message = message, Issues = issues ?? [], Warnings = warnings ?? [], Screen = screen };

    public static ScreenCommandResult Ok(ProcessScreen screen, IReadOnlyList<ScreenIssue>? warnings = null,
        int? versionId = null, bool snapshotFailed = false) =>
        new() { Success = true, Screen = screen, Warnings = warnings ?? [], ConfigVersionId = versionId, SnapshotFailed = snapshotFailed };
}

public sealed class ScreenCommandService(ProjectService projects, ScreenPublicationNotifier notifier)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ScreenCommandResult> CreateAsync(
        ConfigDbContext db, RuntimeDbContext runtime, string? name, int? areaId,
        string actorName, int? actorUserId, CancellationToken cancellationToken)
    {
        if (!ValidName(name)) return ScreenCommandResult.Failed(ScreenCommandError.InvalidName, "画面名称须为 1～100 个字符");
        if (areaId is int area && !await db.Areas.AnyAsync(x => x.Id == area, cancellationToken))
            return ScreenCommandResult.Failed(ScreenCommandError.AreaMissing, "区域不存在");

        var now = ScadaTime.NowMs();
        var screen = new ProcessScreen
        {
            Name = name!.Trim(),
            AreaId = areaId,
            DraftJson = JsonSerializer.Serialize(new ScreenDocument(), Json),
            UpdatedTs = now,
            UpdatedBy = actorName
        };
        db.ProcessScreens.Add(screen);
        await db.SaveChangesAsync(cancellationToken);
        Audit(runtime, actorName, actorUserId, $"创建画面 {screen.Name}", screen.Id, now);
        await runtime.SaveChangesAsync(cancellationToken);
        return ScreenCommandResult.Ok(screen);
    }

    public async Task<ScreenCommandResult> SaveDraftAsync(
        ConfigDbContext db, RuntimeDbContext runtime, int screenId, string? name, int revision,
        ScreenDocument document, int? areaId, bool updateArea, string actorName, int? actorUserId,
        CancellationToken cancellationToken)
    {
        if (!ValidName(name)) return ScreenCommandResult.Failed(ScreenCommandError.InvalidName, "画面名称须为 1～100 个字符");
        if (document is null) return ScreenCommandResult.Failed(ScreenCommandError.EmptyDocument, "画面内容不能为空");
        var shapeIssues = ScreenValidator.Validate(document);
        if (shapeIssues.Count > 0)
            return ScreenCommandResult.Failed(ScreenCommandError.InvalidDocument, "画面格式无效", shapeIssues);

        var screen = await db.ProcessScreens.FirstOrDefaultAsync(x => x.Id == screenId, cancellationToken);
        if (screen is null) return ScreenCommandResult.Failed(ScreenCommandError.NotFound, "Screen does not exist.");
        if (screen.Revision != revision)
            return ScreenCommandResult.Failed(ScreenCommandError.Conflict,
                $"Screen was changed by another editor (current revision {screen.Revision}); reload and retry.",
                screen: screen);

        var nextArea = updateArea ? areaId : screen.AreaId;
        if (nextArea is int next && !await db.Areas.AnyAsync(x => x.Id == next, cancellationToken))
            return ScreenCommandResult.Failed(ScreenCommandError.AreaMissing, "区域不存在", screen: screen);
        if (await ScreenAreaRules.MismatchAsync(db, nextArea, document, cancellationToken) is string mismatch)
            return ScreenCommandResult.Failed(ScreenCommandError.AreaMismatch, mismatch, screen: screen);

        var now = ScadaTime.NowMs();
        screen.AreaId = nextArea;
        screen.Name = name!.Trim();
        screen.DraftJson = JsonSerializer.Serialize(document, Json);
        screen.DraftDirty = true;
        screen.Revision++;
        screen.UpdatedTs = now;
        screen.UpdatedBy = actorName;
        Audit(runtime, actorName, actorUserId, $"保存画面 {screen.Name} r{screen.Revision}", screen.Id, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ScreenCommandResult.Failed(ScreenCommandError.Conflict,
                $"Screen was changed by another editor (current revision {screen.Revision}); reload and retry.",
                screen: screen);
        }
        return ScreenCommandResult.Ok(screen);
    }

    public async Task<ScreenCommandResult> PublishAsync(
        ConfigDbContext db, RuntimeDbContext runtime, int screenId, int revision, int? rollbackToRevision,
        string? note, string actorName, int? actorUserId, CancellationToken cancellationToken)
    {
        var text = note?.Trim() ?? "";
        if (text.Length > 500 || rollbackToRevision is not null && text.Length == 0)
            return ScreenCommandResult.Failed(ScreenCommandError.InvalidNote,
                rollbackToRevision is null ? "发布说明最多 500 个字符" : "回退说明须为 1～500 个字符");
        if (rollbackToRevision is <= 0)
            return ScreenCommandResult.Failed(ScreenCommandError.InvalidNote, "目标版本须为正整数");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var screen = await db.ProcessScreens.FirstOrDefaultAsync(x => x.Id == screenId, cancellationToken);
        if (screen is null) return ScreenCommandResult.Failed(ScreenCommandError.NotFound, "Screen does not exist.");
        if (screen.Revision != revision)
            return ScreenCommandResult.Failed(ScreenCommandError.Conflict,
                $"Screen was changed by another editor (current revision {screen.Revision}); reload and retry.",
                screen: screen);

        var json = screen.DraftJson;
        var name = screen.Name;
        if (rollbackToRevision is int target)
        {
            var version = await db.ProcessScreenVersions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ScreenId == screenId && x.Revision == target, cancellationToken);
            if (version is null) return ScreenCommandResult.Failed(ScreenCommandError.VersionNotFound, "Screen version does not exist.");
            json = version.DocumentJson;
            name = version.Name;
        }

        ScreenDocument? document;
        try { document = JsonSerializer.Deserialize<ScreenDocument>(json, Json); }
        catch (JsonException ex)
        {
            return ScreenCommandResult.Failed(ScreenCommandError.Unreadable, "画面内容无法解析: " + ex.Message, screen: screen);
        }
        if (document is null)
            return ScreenCommandResult.Failed(ScreenCommandError.EmptyDocument, "画面内容不能为空", screen: screen);
        if (await ScreenAreaRules.MismatchAsync(db, screen.AreaId, document, cancellationToken) is string mismatch)
            return ScreenCommandResult.Failed(ScreenCommandError.AreaMismatch, mismatch, screen: screen);

        IReadOnlyList<ScreenIssue> warnings = [];
        if (rollbackToRevision is not null)
        {
            var issues = await ScreenPublication.ValidatePublishedAsync(db, document, screenId, cancellationToken);
            if (issues.Count > 0)
            {
                var message = ScreenPublication.RequiresRepublish(document) ? ScreenPublication.RepublishMessage : "回退校验未通过";
                return ScreenCommandResult.Failed(ScreenCommandError.ValidationFailed, message, issues, screen: screen);
            }
        }
        else
        {
            var publication = await ScreenPublication.PrepareAsync(db, document, screenId, cancellationToken);
            if (publication.Issues.Count > 0)
                return ScreenCommandResult.Failed(ScreenCommandError.ValidationFailed, "发布校验未通过", publication.Issues, publication.Warnings, screen);
            warnings = publication.Warnings;
            json = JsonSerializer.Serialize(publication.Document, Json);
        }

        var now = ScadaTime.NowMs();
        await ProcessScreenHistory.PreserveCurrentAsync(db, screen, cancellationToken);
        screen.PublishedJson = json;
        screen.PublishedName = name;
        screen.Revision++;
        screen.PublishedRevision = screen.Revision;
        screen.DraftDirty = rollbackToRevision is not null;
        screen.UpdatedTs = now;
        screen.UpdatedBy = actorName;
        var kind = rollbackToRevision is null ? "publish" : "rollback";
        var action = rollbackToRevision is int from
            ? $"回退画面 {name} r{from} → r{screen.Revision}：{text}"
            : $"发布画面 {name} r{screen.Revision}";
        ProcessScreenHistory.Record(db, screen, now, actorName, text, kind, rollbackToRevision);
        Audit(runtime, actorName, actorUserId, action, screen.Id, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await ProcessScreenHistory.TrimAsync(db, screen.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ScreenCommandResult.Failed(ScreenCommandError.Conflict,
                $"Screen was changed by another editor (current revision {screen.Revision}); reload and retry.",
                screen: screen);
        }

        await notifier.NotifyAsync(screen.Id, screen.PublishedRevision, screen.PublishedName, CancellationToken.None);
        int? versionId = null;
        var snapshotFailed = false;
        try
        {
            versionId = (await projects.SnapshotAsync("screen-" + kind, action, actorUserId, actorName, CancellationToken.None)).Id;
        }
        catch (Exception ex)
        {
            projects.ReportSnapshotFailure(action, ex);
            snapshotFailed = true;
        }
        return ScreenCommandResult.Ok(screen, warnings, versionId, snapshotFailed);
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 100;

    private static void Audit(RuntimeDbContext runtime, string actorName, int? actorUserId, string action, int screenId, long ts) =>
        runtime.OperationLogs.Add(new OperationLog
        {
            UserId = actorUserId, UserName = actorName, Ts = ts, Action = action,
            Result = "成功", TargetType = "ProcessScreen", TargetId = screenId.ToString()
        });
}
