using Scada.Core.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record ScreenCreateRequest(string Name);
public sealed record ScreenSaveRequest(string Name, int Revision, ScreenDocument Document);
public sealed record ScreenRevisionRequest(int Revision, string? Note = null);
public sealed record ScreenRollbackRequest(int Revision, int TargetRevision, string? Note);
public sealed record ScreenCommandRequest(int PublishedRevision, Guid? RequestId = null, bool Confirmed = false, double? Value = null, string? Operation = null, string? Phase = null);

public static class ScreenEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapScreenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/screens").RequireAuthorization();

        group.MapGet("", async (bool? draft, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (draft == true && !(await http.GetAccessAsync()).HasGlobal(Permission.Configure)) return Results.Forbid();
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.ProcessScreens.AsNoTracking();
            if (draft != true) query = query.Where(x => x.PublishedJson != null);
            var screens = await query.OrderBy(x => x.Id).Select(x => new
            {
                x.Id, Name = draft == true ? x.Name : x.PublishedName,
                Revision = draft == true ? x.Revision : x.PublishedRevision,
                x.PublishedRevision, x.UpdatedTs, x.UpdatedBy,
                HasDraftChanges = draft == true && x.DraftDirty
            }).ToListAsync(ct);
            // 列表直接带布局引用数：同一布局对同一画面只计一次；草稿列表合并草稿与已发布引用，运行列表只看已发布版本。
            var layouts = draft == true
                ? await db.HmiLayouts.AsNoTracking().Select(x => new { x.Name, x.PublishedName, Draft = (string?)x.DocumentJson, Published = x.PublishedJson }).ToListAsync(ct)
                : await db.HmiLayouts.AsNoTracking().Where(x => x.PublishedJson != null).Select(x => new { x.Name, x.PublishedName, Draft = (string?)null, Published = x.PublishedJson }).ToListAsync(ct);
            var layoutCounts = new Dictionary<int, int>();
            var layoutLabels = new Dictionary<int, List<string>>();
            foreach (var layout in layouts)
            {
                var draftIds = draft == true ? new HashSet<int>(ScreenRefs(layout.Draft)) : new HashSet<int>();
                var publishedIds = new HashSet<int>(ScreenRefs(layout.Published));
                var screenIds = new HashSet<int>(draftIds);
                screenIds.UnionWith(publishedIds);
                foreach (var screenId in screenIds)
                {
                    layoutCounts[screenId] = layoutCounts.GetValueOrDefault(screenId) + 1;
                    var label = draftIds.Contains(screenId) ? layout.Name : string.IsNullOrWhiteSpace(layout.PublishedName) ? layout.Name : layout.PublishedName;
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    if (!layoutLabels.TryGetValue(screenId, out var names)) layoutLabels[screenId] = names = new List<string>();
                    if (!names.Contains(label)) names.Add(label);
                }
            }
            return Results.Ok(screens.Select(x => new
            {
                x.Id, x.Name, x.Revision, x.PublishedRevision, x.UpdatedTs, x.UpdatedBy, x.HasDraftChanges,
                LayoutCount = layoutCounts.GetValueOrDefault(x.Id),
                LayoutNames = layoutLabels.TryGetValue(x.Id, out var names) ? names.ToArray() : Array.Empty<string>()
            }));
        });

        group.MapGet("/{id:int}", async (int id, bool? draft, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (draft == true && !(await http.GetAccessAsync()).HasGlobal(Permission.Configure)) return Results.Forbid();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var screen = await db.ProcessScreens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null || draft != true && screen.PublishedJson is null) return Results.NotFound();
            var document = JsonSerializer.Deserialize<ScreenDocument>(draft == true ? screen.DraftJson : screen.PublishedJson!, Json)!;
            var requiresRepublish = draft != true && Screens.ScreenPublication.RequiresRepublish(document);
            return Results.Ok(new
            {
                screen.Id, Name = draft == true ? screen.Name : screen.PublishedName,
                Revision = draft == true ? screen.Revision : screen.PublishedRevision,
                screen.PublishedRevision, screen.UpdatedTs, screen.UpdatedBy,
                Document = document, RequiresRepublish = requiresRepublish,
                Message = requiresRepublish ? Screens.ScreenPublication.RepublishMessage : null
            });
        });

        group.MapPost("", async (ScreenCreateRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (!ValidName(request.Name)) return EndpointHelpers.BadRequest("画面名称须为 1～100 个字符");
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var screen = new ProcessScreen
            {
                Name = request.Name.Trim(), DraftJson = JsonSerializer.Serialize(new ScreenDocument(), Json),
                UpdatedTs = ScadaTime.NowMs(), UpdatedBy = http.User.GetUserName()
            };
            db.ProcessScreens.Add(screen);
            AuditAction(runtime, http, $"创建画面 {screen.Name}");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/screens/{screen.Id}", new { screen.Id, screen.Revision });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        group.MapPut("/{id:int}", async (int id, ScreenSaveRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (!ValidName(request.Name)) return EndpointHelpers.BadRequest("画面名称须为 1～100 个字符");
            if (request.Document is null) return EndpointHelpers.BadRequest("画面内容不能为空");
            var shapeIssues = ScreenValidator.Validate(request.Document);
            if (shapeIssues.Count > 0) return Results.BadRequest(new { message = "画面格式无效", issues = shapeIssues });
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var screen = await db.ProcessScreens.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (screen.Revision != request.Revision) return Conflict();
            screen.Name = request.Name.Trim();
            screen.DraftJson = JsonSerializer.Serialize(request.Document, Json);
            screen.DraftDirty = true;
            screen.Revision++;
            screen.UpdatedTs = ScadaTime.NowMs();
            screen.UpdatedBy = http.User.GetUserName();
            AuditAction(runtime, http, $"保存画面 {screen.Name} r{screen.Revision}");
            try { await db.SaveChangesAsync(ct); await runtime.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            return Results.Ok(new { screen.Id, screen.Revision });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        group.MapPost("/validate", async (ScreenDocument document, int? screenId, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(new { issues = await ValidateReferencesAsync(db, document, screenId, ct) });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        group.MapGet("/{id:int}/versions", async (int id, int? limit, int? beforeRevision, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (beforeRevision is <= 0) return EndpointHelpers.BadRequest("版本游标须为正整数");
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.ProcessScreens.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var query = db.ProcessScreenVersions.AsNoTracking().Where(x => x.ScreenId == id);
            if (beforeRevision is int before) query = query.Where(x => x.Revision < before);
            return Results.Ok(await query.OrderByDescending(x => x.Revision).Take(Math.Clamp(limit ?? 50, 1, 200))
                .Select(x => new { x.Revision, x.Name, x.PublishedTs, x.PublishedBy, x.Note, x.Kind, x.RestoredFromRevision }).ToListAsync(ct));
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        group.MapGet("/{id:int}/versions/{revision:int}", async (int id, int revision, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var version = await db.ProcessScreenVersions.AsNoTracking().SingleOrDefaultAsync(x => x.ScreenId == id && x.Revision == revision, ct);
            if (version is null) return Results.NotFound();
            ScreenDocument? document;
            try { document = JsonSerializer.Deserialize<ScreenDocument>(version.DocumentJson, Json); }
            catch (JsonException) { return EndpointHelpers.BadRequest("历史画面内容无法解析"); }
            if (document is null) return EndpointHelpers.BadRequest("历史画面内容不能为空");
            return Results.Ok(new
            {
                version.Revision, version.Name, version.PublishedTs, version.PublishedBy, version.Note, version.Kind, version.RestoredFromRevision,
                Document = document, RequiresRepublish = Screens.ScreenPublication.RequiresRepublish(document)
            });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        group.MapGet("/{id:int}/usage", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.ProcessScreens.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var layouts = await db.HmiLayouts.AsNoTracking().ToListAsync(ct);
            bool References(string? json) => json is not null && JsonSerializer.Deserialize<HmiLayoutDocument>(json, Json) is { } doc
                && doc.Monitors.Any(m => m.Regions.Any(r => r.Kind == "screen" && r.ScreenId == id));
            var affected = layouts.Select(row => new
            {
                row.Id, row.Name, row.PublishedName,
                InDraft = References(row.DocumentJson), InPublished = References(row.PublishedJson)
            }).Where(x => x.InDraft || x.InPublished).ToList();
            var layoutIds = affected.Select(x => x.Id).ToHashSet();
            var stations = await db.HmiWorkstations.AsNoTracking()
                .Where(x => x.LayoutId != null && layoutIds.Contains(x.LayoutId.Value))
                .Select(x => new { x.Id, x.Name, x.LayoutId }).ToListAsync(ct);
            var instances = await db.ObjectInstances.AsNoTracking()
                .Where(x => x.ScreenId == id).OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Name }).ToListAsync(ct);
            var published = await db.ProcessScreens.AsNoTracking()
                .Where(x => x.Id != id && x.PublishedJson != null)
                .Select(x => new { x.Id, x.Name, x.PublishedName, x.PublishedJson }).ToListAsync(ct);
            var referencedBy = new List<(int Id, string Name)>();
            var unreadable = false;
            foreach (var row in published)
            {
                if (PublishedReferences(row.PublishedJson!, id, out var bad))
                    referencedBy.Add((row.Id, string.IsNullOrWhiteSpace(row.PublishedName) ? row.Name : row.PublishedName));
                if (bad) unreadable = true;
            }
            return Results.Ok(new
            {
                ScreenId = id, Layouts = affected, Workstations = stations, ObjectInstances = instances,
                ReferencedBy = referencedBy.Select(x => new { x.Id, x.Name }).ToList(), Unreadable = unreadable
            });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
        group.MapPost("/{id:int}/publish", (int id, ScreenRevisionRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, Scada.Server.Projects.ProjectService projects, CancellationToken ct) =>
            PublishAsync(id, request.Revision, null, request.Note, http, factory, runtimeFactory, projects, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);

        group.MapPost("/{id:int}/rollback", (int id, ScreenRollbackRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, Scada.Server.Projects.ProjectService projects, CancellationToken ct) =>
            PublishAsync(id, request.Revision, request.TargetRevision, request.Note, http, factory, runtimeFactory, projects, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);

        group.MapDelete("/{id:int}", async (int id, int revision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var screen = await db.ProcessScreens.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (screen.Revision != revision) return Conflict();
            if (await db.ObjectInstances.AnyAsync(x => x.ScreenId == id, ct))
                return EndpointHelpers.BadRequest("此面板仍属于模板对象，请先解除对象关联");
            var others = await db.ProcessScreens.Where(x => x.Id != id && x.PublishedJson != null).ToListAsync(ct);
            if (others.Any(x => JsonSerializer.Deserialize<ScreenDocument>(x.PublishedJson!, Json)!.Nodes
                .Any(n => n.Action.Type is "navigate" or "popup" && n.Action.ScreenId == id)))
                return EndpointHelpers.BadRequest("其他已发布画面引用了此画面，请先修改并发布引用方");
            var layouts = await db.HmiLayouts.AsNoTracking().ToListAsync(ct);
            bool ReferencesScreen(string? json) => json is not null && JsonSerializer.Deserialize<HmiLayoutDocument>(json, Json)?.Monitors.Any(m => m.Regions.Any(r => r.Kind == "screen" && r.ScreenId == id)) == true;
            if (layouts.FirstOrDefault(l => ReferencesScreen(l.DocumentJson) || ReferencesScreen(l.PublishedJson)) is { } usedBy)
                return EndpointHelpers.BadRequest($"运行布局 {usedBy.Name} 的草稿或已发布版本引用了此画面，请先修改草稿并发布布局");
            db.ProcessScreens.Remove(screen);
            AuditAction(runtime, http, $"删除画面 {screen.Name}");
            try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); await runtime.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        group.MapPost("/{id:int}/actions/{nodeId}", (int id, string nodeId, ScreenCommandRequest request,
            HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ControlCommandService commands, MomentaryControlService momentary, CancellationToken ct) =>
            Screens.ScreenActions.ExecuteAsync(id, nodeId, request, http, factory, commands, momentary, ct)).RequireAuthorization(AccessPolicies.Control);
    }

    private static async Task<IResult> PublishAsync(int id, int revision, int? targetRevision, string? note, HttpContext http,
        IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, Scada.Server.Projects.ProjectService projects, CancellationToken ct)
    {
        var text = note?.Trim() ?? "";
        if (text.Length > 500 || targetRevision is not null && text.Length == 0)
            return EndpointHelpers.BadRequest(targetRevision is null ? "发布说明最多 500 个字符" : "回退说明须为 1～500 个字符");
        if (targetRevision is <= 0) return EndpointHelpers.BadRequest("目标版本须为正整数");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var screen = await db.ProcessScreens.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (screen is null) return Results.NotFound();
        if (screen.Revision != revision) return Conflict();
        var json = screen.DraftJson;
        var name = screen.Name;
        if (targetRevision is int target)
        {
            var version = await db.ProcessScreenVersions.AsNoTracking().SingleOrDefaultAsync(x => x.ScreenId == id && x.Revision == target, ct);
            if (version is null) return Results.NotFound();
            json = version.DocumentJson; name = version.Name;
        }
        ScreenDocument? document;
        try { document = JsonSerializer.Deserialize<ScreenDocument>(json, Json); }
        catch (JsonException) { return EndpointHelpers.BadRequest("画面内容无法解析"); }
        if (document is null) return EndpointHelpers.BadRequest("画面内容不能为空");
        if (targetRevision is not null)
        {
            var issues = await Screens.ScreenPublication.ValidatePublishedAsync(db, document, id, ct);
            if (issues.Count > 0) return Results.BadRequest(new
            {
                message = Screens.ScreenPublication.RequiresRepublish(document) ? Screens.ScreenPublication.RepublishMessage : "回退校验未通过", issues
            });
        }
        else
        {
            var publication = await Screens.ScreenPublication.PrepareAsync(db, document, id, ct);
            if (publication.Issues.Count > 0) return Results.BadRequest(new { message = "发布校验未通过", issues = publication.Issues });
            json = JsonSerializer.Serialize(publication.Document, Json);
        }
        await ProcessScreenHistory.PreserveCurrentAsync(db, screen, ct);
        screen.PublishedJson = json; screen.PublishedName = name;
        // Publishing changes the edit revision, but rollback must not replace the draft.
        screen.Revision++;
        screen.PublishedRevision = screen.Revision;
        screen.DraftDirty = targetRevision is not null;
        screen.UpdatedTs = ScadaTime.NowMs(); screen.UpdatedBy = http.User.GetUserName();
        var kind = targetRevision is null ? "publish" : "rollback";
        var action = targetRevision is int from ? $"回退画面 {name} r{from} → r{screen.Revision}：{text}" : $"发布画面 {name} r{screen.Revision}";
        ProcessScreenHistory.Record(db, screen, screen.UpdatedTs, screen.UpdatedBy, text, kind, targetRevision);
        AuditAction(runtime, http, action);
        try
        {
            await db.SaveChangesAsync(ct);
            await ProcessScreenHistory.TrimAsync(db, screen.Id, ct);
            await transaction.CommitAsync(ct);
            await runtime.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        int? versionId = null; string? warning = null;
        try { versionId = (await projects.SnapshotAsync("screen-" + kind, action, http.User.GetUserId(), http.User.GetUserName(), CancellationToken.None)).Id; }
        catch (Exception ex)
        {
            projects.ReportSnapshotFailure(action, ex);
            warning = "运行画面已更新，但自动创建配置快照失败，请在工程管理中手动创建快照；详情见系统告警";
        }
        return Results.Ok(new { screen.Id, screen.Revision, screen.PublishedRevision, versionId, warning });
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 100;
    // Delete refuses a published navigate/popup target. Usage reports the same set so the confirm dialog can name it.
    private static bool PublishedReferences(string json, int screenId, out bool unreadable)
    {
        unreadable = false;
        ScreenDocument? document;
        try { document = JsonSerializer.Deserialize<ScreenDocument>(json, Json); }
        catch (JsonException) { unreadable = true; return false; }
        return document?.Nodes?.Any(node => node?.Action?.Type is "navigate" or "popup" && node.Action.ScreenId == screenId) == true;
    }
    private static int[] ScreenRefs(string? json)
    {
        if (json is null) return [];
        HmiLayoutDocument? layout;
        try { layout = JsonSerializer.Deserialize<HmiLayoutDocument>(json, Json); } catch (JsonException) { return []; }
        return layout?.Monitors.SelectMany(m => m.Regions)
            .Where(r => r.Kind == "screen" && r.ScreenId is > 0).Select(r => r.ScreenId!.Value).ToArray() ?? [];
    }
    private static IResult Conflict() => Results.Conflict(new { message = "画面已被其他窗口修改，请重新打开并合并修改" });
    internal static async Task<IReadOnlyList<ScreenIssue>> ValidateReferencesAsync(ConfigDbContext db, ScreenDocument document, int? selfId, CancellationToken ct) =>
        (await Screens.ScreenPublication.PrepareAsync(db, document, selfId, ct)).Issues;
    private static void AuditAction(RuntimeDbContext runtime, HttpContext http, string action) => runtime.OperationLogs.Add(new OperationLog
    {
        UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(), Action = action, Result = "成功"
    });
}
