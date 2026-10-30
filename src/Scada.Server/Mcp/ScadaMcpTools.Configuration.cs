using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Endpoints;

namespace Scada.Server.Mcp;

// 组态写工具：画面、运行布局与对象模板。草稿保存与发布分开：草稿不改变运行画面，
// 发布才影响操作员 HMI；全部以 mcp:lan 执行并写操作审计，revision 冲突与校验失败返回 success=false。
public sealed partial class ScadaMcpTools
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "create_screen", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create one empty screen draft as mcp:lan and return its screenId and revision. Drafts do not affect the operator HMI. Write the document with save_screen_draft, then publish_screen.")]
    public async Task<McpScreenSaved> CreateScreen(string name, CancellationToken cancellationToken)
    {
        var text = RequiredText(name, 100, "name");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = ScadaTime.NowMs();
        var screen = new ProcessScreen
        {
            Name = text, DraftJson = JsonSerializer.Serialize(new ScreenDocument(), ConfigJson),
            UpdatedTs = now, UpdatedBy = McpActorName
        };
        db.ProcessScreens.Add(screen);
        await db.SaveChangesAsync(cancellationToken);
        db.OperationLogs.Add(Audit($"创建画面 {screen.Name}", "ProcessScreen", screen.Id, "成功", null, null, now));
        await db.SaveChangesAsync(cancellationToken);
        return new(screen.Id, true, "Screen draft created.", screen.Revision, screen.PublishedRevision, [], now);
    }

    [McpServerTool(Name = "save_screen_draft", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Replace the draft document of one existing screen as mcp:lan with an optimistic revision check. Structural validation only; publish_screen also checks referenced tags and screen IDs. Drafts do not affect the running HMI. success=false lists revision conflicts or validation issues.")]
    public async Task<McpScreenSaved> SaveScreenDraft(int screenId, int revision, string name, ScreenDocument document, CancellationToken cancellationToken)
    {
        if (screenId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "screenId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        var text = RequiredText(name, 100, "name");
        if (document is null) throw new McpToolException(McpErrorCodes.InvalidArgument, "document is required.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var screen = await db.ProcessScreens.SingleOrDefaultAsync(x => x.Id == screenId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Screen does not exist.");
        var now = ScadaTime.NowMs();
        if (screen.Revision != revision)
            return new(screen.Id, false, $"Screen was changed by another editor (current revision {screen.Revision}); reload and retry.",
                screen.Revision, screen.PublishedRevision, [], now);
        var issues = ScreenValidator.Validate(document).Select(Describe).ToArray();
        if (issues.Length > 0)
            return new(screen.Id, false, "Screen document is invalid.", screen.Revision, screen.PublishedRevision, issues, now);
        screen.Name = text;
        screen.DraftJson = JsonSerializer.Serialize(document, ConfigJson);
        screen.Revision++;
        screen.UpdatedTs = now;
        screen.UpdatedBy = McpActorName;
        db.OperationLogs.Add(Audit($"保存画面 {screen.Name} r{screen.Revision}", "ProcessScreen", screen.Id, "成功", null, null, now));
        await db.SaveChangesAsync(cancellationToken);
        return new(screen.Id, true, "Draft saved.", screen.Revision, screen.PublishedRevision, [], now);
    }

    [McpServerTool(Name = "publish_screen", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Publish one screen draft as mcp:lan after full reference validation (tags, control targets, published navigation targets and template instances). Publishing changes the operator HMI and attempts a configuration snapshot; a snapshot failure is returned as a warning, not a failed publish. success=false on revision conflicts or validation issues.")]
    public async Task<McpScreenPublished> PublishScreen(int screenId, int revision, CancellationToken cancellationToken)
    {
        if (screenId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "screenId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var screen = await db.ProcessScreens.SingleOrDefaultAsync(x => x.Id == screenId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Screen does not exist.");
        var now = ScadaTime.NowMs();
        if (screen.Revision != revision)
            return new(screen.Id, false, $"Screen was changed by another editor (current revision {screen.Revision}); reload and retry.",
                screen.Revision, screen.PublishedRevision, null, null, [], now);
        ScreenDocument? document;
        try { document = JsonSerializer.Deserialize<ScreenDocument>(screen.DraftJson, ConfigJson); }
        catch (JsonException ex) { return new(screen.Id, false, "Stored draft cannot be parsed: " + ex.Message, screen.Revision, screen.PublishedRevision, null, null, [], now); }
        if (document is null) return new(screen.Id, false, "Stored draft is empty.", screen.Revision, screen.PublishedRevision, null, null, [], now);
        var issues = (await ScreenEndpoints.ValidateReferencesAsync(db, document, screen.Id, cancellationToken)).Select(Describe).ToArray();
        if (issues.Length > 0)
            return new(screen.Id, false, "Publish validation failed.", screen.Revision, screen.PublishedRevision, null, null, issues, now);
        screen.PublishedJson = screen.DraftJson;
        screen.PublishedName = screen.Name;
        screen.Revision++;
        screen.PublishedRevision = screen.Revision;
        screen.UpdatedTs = now;
        screen.UpdatedBy = McpActorName;
        db.OperationLogs.Add(Audit($"发布画面 {screen.Name} r{screen.Revision}", "ProcessScreen", screen.Id, "成功", null, null, now));
        await db.SaveChangesAsync(cancellationToken);
        int? versionId = null;
        string? warning = null;
        try
        {
            versionId = (await projects.SnapshotAsync("screen-publish", $"发布画面 {screen.Name} r{screen.Revision}", null, McpActorName, CancellationToken.None)).Id;
        }
        catch (Exception ex)
        {
            projects.ReportSnapshotFailure($"发布画面 {screen.Name} r{screen.Revision}", ex);
            warning = "Screen published, but the automatic configuration snapshot failed; create one manually in project management. Details are in system alarms.";
        }
        return new(screen.Id, true, "Screen published.", screen.Revision, screen.PublishedRevision, versionId, warning, [], ScadaTime.NowMs());
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
        var now = ScadaTime.NowMs();
        var row = layoutId is int layoutKey ? await db.HmiLayouts.SingleOrDefaultAsync(x => x.Id == layoutKey, cancellationToken) : null;
        if (layoutId is not null && row is null) throw new McpToolException(McpErrorCodes.NotFound, "Layout does not exist.");
        if (row is not null && row.Revision != revision)
            return new(row.Id, false, $"Layout was changed by another editor (current revision {row.Revision}); reload and retry.", row.Revision, row.PublishedRevision, row.PublishedJson is null, now);
        if (HmiLayoutValidator.ValidateDraft(document) is string error)
            return new(row?.Id ?? 0, false, error, row?.Revision ?? 0, row?.PublishedRevision, row?.PublishedJson is null, now);
        if (await db.HmiLayouts.AnyAsync(x => x.Name == text && x.Id != (layoutId ?? 0), cancellationToken))
            return new(row?.Id ?? 0, false, "Layout name already exists.", row?.Revision ?? 0, row?.PublishedRevision, row?.PublishedJson is null, now);
        var creating = row is null;
        if (row is null)
        {
            row = new HmiLayout();
            db.HmiLayouts.Add(row);
            row.Revision = 1;
        }
        else row.Revision++;
        row.Name = text;
        row.DocumentJson = JsonSerializer.Serialize(document, HmiLayoutEndpoints.Json);
        row.UpdatedTs = now;
        row.UpdatedBy = McpActorName;
        await db.SaveChangesAsync(cancellationToken);
        db.OperationLogs.Add(Audit($"{(creating ? "新建" : "保存")}运行布局草稿 {row.Name}", "HmiLayout", row.Id, "成功", null, null, now));
        await db.SaveChangesAsync(cancellationToken);
        var hasDraft = row.PublishedJson is null || row.Name != row.PublishedName || row.DocumentJson != row.PublishedJson;
        return new(row.Id, true, creating ? "Layout draft created." : "Layout draft saved.", row.Revision, row.PublishedRevision, hasDraft, now);
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
        var row = await db.HmiLayouts.SingleOrDefaultAsync(x => x.Id == layoutId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Layout does not exist.");
        var now = ScadaTime.NowMs();
        var kind = rollbackToRevision is null ? "publish" : "rollback";
        if (row.Revision != revision)
            return new(row.Id, false, $"Layout was changed by another editor (current revision {row.Revision}); reload and retry.", row.Revision, row.PublishedRevision, kind, text, now);
        var name = row.Name;
        var documentJson = row.DocumentJson;
        if (rollbackToRevision is int target)
        {
            var version = await db.HmiLayoutVersions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.LayoutId == layoutId && x.Revision == target, cancellationToken)
                ?? throw new McpToolException(McpErrorCodes.NotFound, "Layout version does not exist.");
            name = version.Name;
            documentJson = version.DocumentJson;
        }
        HmiLayoutDocument? document;
        try { document = JsonSerializer.Deserialize<HmiLayoutDocument>(documentJson, HmiLayoutEndpoints.Json); }
        catch (JsonException ex) { return new(row.Id, false, "Layout document cannot be parsed: " + ex.Message, row.Revision, row.PublishedRevision, kind, text, now); }
        var published = (await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(cancellationToken)).ToHashSet();
        if (HmiLayoutValidator.Validate(document, published) is string error)
            return new(row.Id, false, error, row.Revision, row.PublishedRevision, kind, text, now);
        row.Revision++;
        row.PublishedName = name;
        row.PublishedJson = documentJson;
        row.PublishedRevision = row.Revision;
        row.PublishedTs = now;
        row.PublishedBy = McpActorName;
        db.HmiLayoutVersions.Add(new HmiLayoutVersion
        {
            LayoutId = row.Id, Revision = row.Revision, Name = name, DocumentJson = documentJson,
            PublishedTs = now, PublishedBy = McpActorName, Note = text,
            RestoredFromRevision = rollbackToRevision, Kind = kind
        });
        db.OperationLogs.Add(Audit(
            rollbackToRevision is int from ? $"回退运行布局 {name} r{from} → r{row.Revision}：{text}" : $"发布运行布局 {name} r{row.Revision}：{text}",
            "HmiLayout", row.Id, "成功", null, null, now));
        await db.SaveChangesAsync(cancellationToken);
        return new(row.Id, true, rollbackToRevision is null ? "Layout published." : "Layout rolled back.", row.Revision, row.PublishedRevision, kind, text, now);
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
        var errors = ObjectTemplateValidator.Validate(document, false).ToArray();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var now = ScadaTime.NowMs();
        var row = templateId is int templateKey ? await db.ObjectTemplates.SingleOrDefaultAsync(x => x.Id == templateKey, cancellationToken) : null;
        if (templateId is not null && row is null) throw new McpToolException(McpErrorCodes.NotFound, "Object template does not exist.");
        if (row is not null && row.Revision != revision)
            return new(row.Id, false, $"Template was changed by another editor (current revision {row.Revision}); reload and retry.", row.Revision, row.LatestVersion, [], now);
        if (errors.Length > 0)
            return new(row?.Id ?? 0, false, "Object template is invalid.", row?.Revision ?? 0, row?.LatestVersion ?? 0, errors, now);
        if (await db.ObjectTemplates.AnyAsync(x => x.Name == text && x.Id != (templateId ?? 0), cancellationToken))
            return new(row?.Id ?? 0, false, "Template name already exists.", row?.Revision ?? 0, row?.LatestVersion ?? 0, [], now);
        var creating = row is null;
        if (row is null)
        {
            row = new DeviceObjectTemplate { Revision = 1 };
            db.ObjectTemplates.Add(row);
        }
        else row.Revision++;
        row.Name = text;
        row.DraftJson = ObjectTemplateJson.Write(document);
        row.UpdatedTs = now;
        row.UpdatedBy = McpActorName;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return new(row.Id, false, "Template name conflicts with another template; reload and retry.", row.Revision, row.LatestVersion, [], now); }
        db.OperationLogs.Add(Audit(creating ? $"创建对象模板：{row.Name}" : $"保存对象模板 {row.Name} r{row.Revision}", "DeviceObjectTemplate", row.Id, "成功", null, null, now));
        await db.SaveChangesAsync(cancellationToken);
        return new(row.Id, true, creating ? "Object template draft created." : "Object template draft saved.", row.Revision, row.LatestVersion, [], now);
    }

    [McpServerTool(Name = "publish_object_template", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Publish one object-template draft as mcp:lan after full validation: tags and alarms, bindings for every placeholder and control output, and published navigation targets. Publishing appends an immutable template version. success=false on revision conflicts or validation errors.")]
    public async Task<McpObjectTemplatePublished> PublishObjectTemplate(int templateId, int revision, CancellationToken cancellationToken)
    {
        if (templateId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "templateId must be positive.");
        if (revision < 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "revision must not be negative.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = await db.ObjectTemplates.SingleOrDefaultAsync(x => x.Id == templateId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Object template does not exist.");
        var now = ScadaTime.NowMs();
        if (row.Revision != revision)
            return new(row.Id, false, $"Template was changed by another editor (current revision {row.Revision}); reload and retry.", row.Revision, row.LatestVersion, [], now);
        ObjectTemplateDocument? document;
        try { document = ObjectTemplateJson.Read<ObjectTemplateDocument>(row.DraftJson); }
        catch (JsonException ex) { return new(row.Id, false, "Stored draft cannot be parsed: " + ex.Message, row.Revision, row.LatestVersion, [], now); }
        var errors = ObjectTemplateValidator.Validate(document, true).ToList();
        var targets = await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(cancellationToken);
        if (document?.Panel?.Nodes?.Any(n => n?.Action?.Type is "navigate" or "popup" && !targets.Contains(n.Action.ScreenId ?? 0)) == true)
            errors.Add("Panel navigation targets must reference published screens.");
        if (errors.Count > 0)
            return new(row.Id, false, "Publish validation failed.", row.Revision, row.LatestVersion, errors, now);
        row.LatestVersion++;
        row.Revision++;
        row.UpdatedTs = now;
        row.UpdatedBy = McpActorName;
        db.ObjectTemplateVersions.Add(new DeviceObjectTemplateVersion
        {
            TemplateId = row.Id, Version = row.LatestVersion, DocumentJson = row.DraftJson,
            CreatedTs = now, CreatedBy = McpActorName
        });
        db.OperationLogs.Add(Audit($"发布对象模板 {row.Name} v{row.LatestVersion}", "DeviceObjectTemplate", row.Id, "成功", null, null, now));
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); }
        catch (DbUpdateException) { return new(row.Id, false, "Template was changed concurrently; reload and retry.", row.Revision, row.LatestVersion, [], now); }
        return new(row.Id, true, "Object template published.", row.Revision, row.LatestVersion, [], ScadaTime.NowMs());
    }

    private static string Describe(ScreenIssue issue) => string.IsNullOrEmpty(issue.NodeId) ? issue.Message : $"{issue.NodeId}: {issue.Message}";

    public sealed record McpScreenSaved(int ScreenId, bool Success, string Message, int Revision, int? PublishedRevision,
        IReadOnlyList<string> Issues, long DataAsOf);
    public sealed record McpScreenPublished(int ScreenId, bool Success, string Message, int Revision, int? PublishedRevision,
        int? ConfigVersionId, string? Warning, IReadOnlyList<string> Issues, long DataAsOf);
    public sealed record McpHmiLayoutSaved(int LayoutId, bool Success, string Message, int Revision, int? PublishedRevision,
        bool HasDraftChanges, long DataAsOf);
    public sealed record McpHmiLayoutPublished(int LayoutId, bool Success, string Message, int Revision, int? PublishedRevision,
        string Kind, string Note, long DataAsOf);
    public sealed record McpObjectTemplateSaved(int TemplateId, bool Success, string Message, int Revision, int LatestVersion,
        IReadOnlyList<string> Errors, long DataAsOf);
    public sealed record McpObjectTemplatePublished(int TemplateId, bool Success, string Message, int Revision, int Version,
        IReadOnlyList<string> Errors, long DataAsOf);
}
