using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Services;

/// <summary>
/// 运行布局草稿与发布的唯一落地点：REST 与 MCP 共用名称/文档/说明校验、重名拒绝、乐观锁、
/// 发布冻结与版本追加、事务和成功审计。授权与 HTTP/MCP 返回形态仍由各入口负责。
/// </summary>
public enum HmiLayoutCommandError
{
    None,
    InvalidName,
    InvalidDocument,
    InvalidNote,
    DuplicateName,
    NotFound,
    VersionNotFound,
    Conflict
}

public sealed class HmiLayoutCommandResult
{
    public bool Success { get; init; }
    public bool Created { get; init; }
    public HmiLayoutCommandError Error { get; init; }
    public string? Message { get; init; }
    public HmiLayout? Layout { get; init; }
    public string Kind { get; init; } = "publish";

    public static HmiLayoutCommandResult Failed(HmiLayoutCommandError error, string? message = null, HmiLayout? layout = null) =>
        new() { Error = error, Message = message, Layout = layout };

    public static HmiLayoutCommandResult Ok(HmiLayout layout, bool created = false, string kind = "publish") =>
        new() { Success = true, Created = created, Layout = layout, Kind = kind };
}

public sealed class HmiLayoutCommandService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string InvalidNameMessage = "名称须为 1～100 个字符";
    public const string DuplicateNameMessage = "名称已存在";
    public const string InvalidNoteMessage = "发布或回退说明须为 1～500 个字符";
    public const string ConflictMessage = "布局已被他人修改，请刷新并合并修改";
    public const string VersionNotFoundMessage = "指定的布局发布版本不存在";

    public async Task<HmiLayoutCommandResult> SaveAsync(
        ConfigDbContext db, RuntimeDbContext runtime, int? layoutId, string? name, HmiLayoutDocument document,
        int revision, string actorName, int? actorUserId, CancellationToken cancellationToken)
    {
        var text = name?.Trim() ?? "";
        if (text.Length is 0 or > 100) return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.InvalidName, InvalidNameMessage);
        if (HmiLayoutValidator.ValidateDraft(document) is string error)
            return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.InvalidDocument, error);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = layoutId is int id ? await db.HmiLayouts.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) : null;
        if (layoutId is not null && row is null) return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.NotFound, "Layout does not exist.");
        if (await db.HmiLayouts.AnyAsync(x => x.Name == text && x.Id != (layoutId ?? 0), cancellationToken))
            return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.DuplicateName, DuplicateNameMessage, row);
        if (row is not null && row.Revision != revision)
            return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.Conflict, ConflictMessage, row);

        var creating = row is null;
        var before = row is null ? null : Snapshot(row);
        var now = ScadaTime.NowMs();
        if (row is null)
        {
            row = new HmiLayout { Revision = 1 };
            db.HmiLayouts.Add(row);
        }
        else row.Revision++;
        row.Name = text;
        row.DocumentJson = JsonSerializer.Serialize(document, Json);
        row.UpdatedTs = now;
        row.UpdatedBy = actorName;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            WriteAudit(runtime, actorName, actorUserId, $"{(creating ? "新建" : "保存")}运行布局草稿 {text}", row.Id, now, before, Snapshot(row));
            await transaction.CommitAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.Conflict, ConflictMessage, row);
        }
        return HmiLayoutCommandResult.Ok(row, created: creating);
    }

    public async Task<HmiLayoutCommandResult> PublishAsync(
        ConfigDbContext db, RuntimeDbContext runtime, int layoutId, int revision, string? note, int? targetRevision,
        string actorName, int? actorUserId, CancellationToken cancellationToken)
    {
        note = note?.Trim();
        if (string.IsNullOrEmpty(note) || note.Length > 500)
            return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.InvalidNote, InvalidNoteMessage);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.HmiLayouts.SingleOrDefaultAsync(x => x.Id == layoutId, cancellationToken);
        if (row is null) return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.NotFound, "Layout does not exist.");
        if (row.Revision != revision) return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.Conflict, ConflictMessage, row);

        var name = row.Name;
        var documentJson = row.DocumentJson;
        if (targetRevision is int restore)
        {
            var version = await db.HmiLayoutVersions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.LayoutId == layoutId && x.Revision == restore, cancellationToken);
            if (version is null)
                return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.VersionNotFound, VersionNotFoundMessage, row);
            name = version.Name;
            documentJson = version.DocumentJson;
        }

        HmiLayoutDocument? document;
        try { document = JsonSerializer.Deserialize<HmiLayoutDocument>(documentJson, Json); }
        catch (JsonException ex)
        {
            return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.InvalidDocument, $"布局文档格式无效：{ex.Message}", row);
        }

        var published = (await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
        if (HmiLayoutValidator.Validate(document, published) is string error)
            return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.InvalidDocument, error, row);

        var now = ScadaTime.NowMs();
        var kind = targetRevision is null ? "publish" : "rollback";
        var before = Snapshot(row);
        row.Revision++;
        row.PublishedName = name;
        row.PublishedJson = documentJson;
        row.PublishedRevision = row.Revision;
        row.PublishedTs = now;
        row.PublishedBy = actorName;
        var screenRevisions = await LayoutScreenRevisionCapture.CaptureAsync(db, documentJson, cancellationToken);
        db.HmiLayoutVersions.Add(new HmiLayoutVersion
        {
            LayoutId = row.Id, Revision = row.Revision, Name = name, DocumentJson = documentJson,
            PublishedTs = now, PublishedBy = actorName, Note = note,
            RestoredFromRevision = targetRevision, Kind = kind, ScreenRevisionsJson = screenRevisions
        });
        var action = targetRevision is int from
            ? $"回退运行布局 {name} r{from} → r{row.Revision}：{note}"
            : $"发布运行布局 {name} r{row.Revision}：{note}";
        WriteAudit(runtime, actorName, actorUserId, action, row.Id, now, before, Snapshot(row));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await runtime.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return HmiLayoutCommandResult.Failed(HmiLayoutCommandError.Conflict, ConflictMessage, row);
        }
        return HmiLayoutCommandResult.Ok(row, kind: kind);
    }

    private static object Snapshot(HmiLayout x) => new
    {
        x.Id, x.Name, x.Revision, x.UpdatedTs, x.UpdatedBy,
        x.PublishedRevision, x.PublishedName, x.PublishedTs, x.PublishedBy
    };

    private static void WriteAudit(RuntimeDbContext runtime, string actorName, int? actorUserId, string action, int layoutId, long ts, object? before, object? after) =>
        runtime.OperationLogs.Add(new OperationLog
        {
            UserId = actorUserId, UserName = actorName, Ts = ts, Action = action,
            TargetType = "HmiLayout", TargetId = layoutId.ToString(),
            Details = Scada.Server.Auth.Audit.Diff(before, after), Result = "成功"
        });
}
