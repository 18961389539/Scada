using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Endpoints;
using Scada.Server.Services;
using static Scada.Server.Mcp.McpActors;
using static Scada.Server.Mcp.McpAuditLog;
using static Scada.Server.Mcp.McpText;

namespace Scada.Server.Mcp;

// 组态写工具：画面、运行布局与对象模板。草稿保存与发布分开：草稿不改变运行画面，
// 发布才影响操作员 HMI；全部以 mcp:lan 执行并写操作审计，revision 冲突与校验失败返回 success=false。
[McpServerToolType]
public sealed class McpConfigurationTools(
    IDbContextFactory<ConfigDbContext> factory,
    IDbContextFactory<RuntimeDbContext> runtimeFactory,
    ScreenCommandService screens,
    HmiLayoutCommandService layouts,
    ObjectTemplateCommandService objectTemplates)
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "create_screen", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create one empty screen draft as mcp:lan and return its screenId and revision. Drafts do not affect the operator HMI. Write the document with save_screen_draft, then publish_screen.")]
    public async Task<McpScreenSaved> CreateScreen(string name, CancellationToken cancellationToken,
        [Description("Optional area ID. Omit to leave the screen unassigned.")] int? areaId = null)
    {
        var text = RequiredText(name, 100, "name");
        if (areaId is int area && area <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "areaId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        var result = await screens.CreateAsync(db, runtime, text, areaId, McpActorName, null, cancellationToken);
        return MapSaved(result, result.Success ? "Screen draft created." : result.Message);
    }

    [McpServerTool(Name = "save_screen_draft", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Replace the draft document of one existing screen as mcp:lan with an optimistic revision check. Structural validation only; publish_screen also checks referenced tags and screen IDs. Drafts do not affect the running HMI. success=false lists revision conflicts or validation issues.")]
    public async Task<McpScreenSaved> SaveScreenDraft(int screenId, int revision, string name, ScreenDocument document, CancellationToken cancellationToken,
        [Description("New area ID; omit to keep the current area.")] int? areaId = null,
        [Description("Set true to clear the screen area. Do not pass areaId at the same time.")] bool clearArea = false)
    {
        if (screenId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "screenId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        var text = RequiredText(name, 100, "name");
        if (document is null) throw new McpToolException(McpErrorCodes.InvalidArgument, "document is required.");
        if (clearArea && areaId is not null)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "Do not pass areaId when clearArea is true.");
        var updateArea = areaId is not null || clearArea;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        var result = await screens.SaveDraftAsync(db, runtime, screenId, text, revision, document, areaId, updateArea, McpActorName, null, cancellationToken);
        return MapSaved(result, result.Success ? "Draft saved." : result.Message);
    }

    [McpServerTool(Name = "publish_screen", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Publish a screen draft as mcp:lan, freezing templates and SVG assets, or roll back to an immutable publication with a required note. Rollback rechecks current references without changing the draft. Every publication appends history and attempts a configuration snapshot; snapshot failure is a warning. success=false on revision conflicts or validation issues.")]
    public async Task<McpScreenPublished> PublishScreen(int screenId, int revision, CancellationToken cancellationToken,
        [Description("Stored publication revision to restore; omit to publish the current draft.")] int? rollbackToRevision = null,
        [Description("Optional publication note, required for rollback; 1-500 characters after trimming.")] string? note = null)
    {
        if (screenId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "screenId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        if (rollbackToRevision is <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "rollbackToRevision must be positive.");
        var text = rollbackToRevision is not null ? RequiredText(note ?? "", 500, "note") : note?.Trim() ?? "";
        if (text.Length > 500) throw new McpToolException(McpErrorCodes.InvalidArgument, "note must not exceed 500 characters.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        var result = await screens.PublishAsync(db, runtime, screenId, revision, rollbackToRevision, text, McpActorName, null, cancellationToken);
        var now = ScadaTime.NowMs();
        if (result.Error is ScreenCommandError.NotFound) throw new McpToolException(McpErrorCodes.NotFound, "Screen does not exist.");
        if (result.Error is ScreenCommandError.VersionNotFound) throw new McpToolException(McpErrorCodes.NotFound, "Screen version does not exist.");
        var screen = result.Screen;
        var issues = result.Issues.Select(Describe).ToArray();
        if (!result.Success)
        {
            return new(screenId, false, result.Message ?? "Publish validation failed.",
                screen?.Revision ?? 0, screen?.PublishedRevision, null, null, issues, now);
        }
        var warning = result.SnapshotFailed
            ? "Screen runtime updated, but the automatic configuration snapshot failed; create one manually in project management. Details are in system alarms."
            : null;
        return new(screen!.Id, true, rollbackToRevision is null ? "Screen published." : "Screen rolled back.",
            screen.Revision, screen.PublishedRevision, result.ConfigVersionId, warning, [], now, result.Warnings.Select(Describe).ToArray());
    }

    [McpServerTool(Name = "save_hmi_layout", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create or replace one HMI layout draft as mcp:lan. Pass layoutId to update an existing layout and its revision; omit both to create. Draft validation allows unpublished screens and empty regions. Drafts do not affect workstations until publish_hmi_layout. success=false on duplicate names, revision conflicts or invalid documents.")]
    public async Task<McpHmiLayoutSaved> SaveHmiLayout(string name, HmiLayoutDocument document, CancellationToken cancellationToken,
        [Description("Existing layout ID; omit to create a new layout.")] int? layoutId = null,
        [Description("Current revision of the layout being updated; ignored when creating.")] int revision = 0)
    {
        var text = RequiredText(name, 100, "name");
        if (layoutId is int id && id <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "layoutId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        var result = await layouts.SaveAsync(db, runtime, layoutId, text, document, revision, McpActorName, null, cancellationToken);
        return MapLayoutSaved(result);
    }

    [McpServerTool(Name = "publish_hmi_layout", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Publish the current draft of one HMI layout, or roll back to a stored layout revision, as mcp:lan with a required note. Every screen region must reference a published screen. Each publish or rollback appends an immutable layout version and affects assigned workstations immediately. success=false on revision conflicts or validation errors.")]
    public async Task<McpHmiLayoutPublished> PublishHmiLayout(int layoutId, int revision, string note, CancellationToken cancellationToken,
        [Description("Stored layout revision to republish; omit to publish the current draft.")] int? rollbackToRevision = null)
    {
        if (layoutId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "layoutId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        var text = RequiredText(note, 500, "note");
        if (rollbackToRevision is int restore && restore <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "rollbackToRevision must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        var result = await layouts.PublishAsync(db, runtime, layoutId, revision, text, rollbackToRevision, McpActorName, null, cancellationToken);
        var now = ScadaTime.NowMs();
        if (result.Error is HmiLayoutCommandError.NotFound) throw new McpToolException(McpErrorCodes.NotFound, "Layout does not exist.");
        if (result.Error is HmiLayoutCommandError.VersionNotFound) throw new McpToolException(McpErrorCodes.NotFound, "Layout version does not exist.");
        var row = result.Layout;
        if (result.Error is HmiLayoutCommandError.Conflict)
            return new(row!.Id, false, $"Layout was changed by another editor (current revision {row.Revision}); reload and retry.", row.Revision, row.PublishedRevision, rollbackToRevision is null ? "publish" : "rollback", text, now);
        if (!result.Success)
            return new(row?.Id ?? layoutId, false, result.Message ?? "Rejected.", row?.Revision ?? 0, row?.PublishedRevision, result.Kind, text, now);
        return new(row!.Id, true, rollbackToRevision is null ? "Layout published." : "Layout rolled back.", row.Revision, row.PublishedRevision, result.Kind, text, now);
    }

    [McpServerTool(Name = "save_object_template", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create or replace one device object-template draft as mcp:lan. Pass templateId and its revision to update; omit both to create. Draft validation checks tag/alarm definitions, panel symbols and bindings; publish_object_template additionally requires complete bindings. success=false on duplicate names, revision conflicts or validation errors.")]
    public async Task<McpObjectTemplateSaved> SaveObjectTemplate(string name, ObjectTemplateDocument document, CancellationToken cancellationToken,
        [Description("Existing template ID; omit to create a new template.")] int? templateId = null,
        [Description("Current revision of the template being updated; ignored when creating.")] int revision = 0)
    {
        var text = RequiredText(name, 100, "name");
        if (templateId is int id && id <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "templateId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        var result = await objectTemplates.SaveAsync(db, runtime, templateId, text, document, revision, McpActorName, null, cancellationToken);
        return MapObjectSaved(result, creating: templateId is null);
    }

    [McpServerTool(Name = "publish_object_template", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Publish one object-template draft as mcp:lan after full validation: tags and alarms, bindings for every placeholder and control output, and published navigation targets. Publishing appends an immutable template version. success=false on revision conflicts or validation errors.")]
    public async Task<McpObjectTemplatePublished> PublishObjectTemplate(int templateId, int revision, CancellationToken cancellationToken)
    {
        if (templateId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "templateId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        var result = await objectTemplates.PublishAsync(db, runtime, templateId, revision, McpActorName, null, cancellationToken);
        var now = ScadaTime.NowMs();
        if (result.Error is ObjectTemplateCommandError.NotFound) throw new McpToolException(McpErrorCodes.NotFound, "Object template does not exist.");
        var row = result.Template;
        if (result.Error is ObjectTemplateCommandError.Conflict)
            return new(row!.Id, false, $"Template was changed by another editor (current revision {row.Revision}); reload and retry.", row.Revision, row.LatestVersion, result.Errors.ToArray(), now);
        if (!result.Success)
            return new(row?.Id ?? templateId, false, result.Message ?? "Publish validation failed.", row?.Revision ?? 0, row?.LatestVersion ?? 0, result.Errors.ToArray(), now);
        return new(row!.Id, true, "Object template published.", row.Revision, row.LatestVersion, [], ScadaTime.NowMs());
    }

    private static McpHmiLayoutSaved MapLayoutSaved(HmiLayoutCommandResult result)
    {
        var now = ScadaTime.NowMs();
        if (result.Error is HmiLayoutCommandError.NotFound) throw new McpToolException(McpErrorCodes.NotFound, "Layout does not exist.");
        var row = result.Layout;
        var hasDraft = row is null || row.PublishedJson is null || row.Name != row.PublishedName || row.DocumentJson != row.PublishedJson;
        if (result.Error is HmiLayoutCommandError.Conflict)
            return new(row!.Id, false, $"Layout was changed by another editor (current revision {row.Revision}); reload and retry.", row.Revision, row.PublishedRevision, hasDraft, now);
        if (result.Error is HmiLayoutCommandError.DuplicateName)
            return new(row?.Id ?? 0, false, "Layout name already exists.", row?.Revision ?? 0, row?.PublishedRevision, row?.PublishedJson is null, now);
        if (!result.Success)
            return new(row?.Id ?? 0, false, result.Message ?? "Rejected.", row?.Revision ?? 0, row?.PublishedRevision, row?.PublishedJson is null, now);
        return new(row!.Id, true, result.Created ? "Layout draft created." : "Layout draft saved.", row.Revision, row.PublishedRevision, hasDraft, now);
    }

    private static McpObjectTemplateSaved MapObjectSaved(ObjectTemplateCommandResult result, bool creating)
    {
        var now = ScadaTime.NowMs();
        if (result.Error is ObjectTemplateCommandError.NotFound) throw new McpToolException(McpErrorCodes.NotFound, "Object template does not exist.");
        var row = result.Template;
        if (result.Error is ObjectTemplateCommandError.Conflict)
            return new(row!.Id, false, $"Template was changed by another editor (current revision {row.Revision}); reload and retry.", row.Revision, row.LatestVersion, result.Errors.ToArray(), now);
        if (result.Error is ObjectTemplateCommandError.DuplicateName)
            return new(row?.Id ?? 0, false, "Template name already exists.", row?.Revision ?? 0, row?.LatestVersion ?? 0, [], now);
        if (!result.Success)
            return new(row?.Id ?? 0, false, result.Message ?? "Object template is invalid.", row?.Revision ?? 0, row?.LatestVersion ?? 0, result.Errors.ToArray(), now);
        return new(row!.Id, true, result.Created ? "Object template draft created." : "Object template draft saved.", row.Revision, row.LatestVersion, [], now);
    }

    private static McpScreenSaved MapSaved(ScreenCommandResult result, string? message)
    {
        var now = ScadaTime.NowMs();
        if (result.Error is ScreenCommandError.NotFound) throw new McpToolException(McpErrorCodes.NotFound, "Screen does not exist.");
        if (result.Error is ScreenCommandError.AreaMissing) throw new McpToolException(McpErrorCodes.NotFound, "Area does not exist.");
        var issues = result.Issues.Select(Describe).ToArray();
        var screen = result.Screen;
        return new(screen?.Id ?? 0, result.Success, message ?? result.Message ?? "Rejected.",
            screen?.Revision ?? 0, screen?.PublishedRevision, issues, now);
    }

    private static string Describe(ScreenIssue issue) => string.IsNullOrEmpty(issue.NodeId) ? issue.Message : $"{issue.NodeId}: {issue.Message}";

    public sealed record McpScreenSaved(int ScreenId, bool Success, string Message, int Revision, int? PublishedRevision,
        IReadOnlyList<string> Issues, long DataAsOf);
    public sealed record McpScreenPublished(int ScreenId, bool Success, string Message, int Revision, int? PublishedRevision,
        int? ConfigVersionId, string? Warning, IReadOnlyList<string> Issues, long DataAsOf, IReadOnlyList<string>? Warnings = null);
    public sealed record McpHmiLayoutSaved(int LayoutId, bool Success, string Message, int Revision, int? PublishedRevision,
        bool HasDraftChanges, long DataAsOf);
    public sealed record McpHmiLayoutPublished(int LayoutId, bool Success, string Message, int Revision, int? PublishedRevision,
        string Kind, string Note, long DataAsOf);
    public sealed record McpObjectTemplateSaved(int TemplateId, bool Success, string Message, int Revision, int LatestVersion,
        IReadOnlyList<string> Errors, long DataAsOf);
    public sealed record McpObjectTemplatePublished(int TemplateId, bool Success, string Message, int Revision, int Version,
        IReadOnlyList<string> Errors, long DataAsOf);
}
