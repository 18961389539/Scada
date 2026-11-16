using Scada.Core.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record ScreenCreateRequest(string Name, int? AreaId = null);
public sealed record ScreenSaveRequest(string Name, int Revision, ScreenDocument Document, int? AreaId = null, bool UpdateArea = false);
public sealed record ScreenRevisionRequest(int Revision, string? Note = null);
public sealed record ScreenRollbackRequest(int Revision, int TargetRevision, string? Note);
public sealed record ScreenRuntimeReportRequest(string Kind, string? Message = null, int? PublishedRevision = null);
public sealed record ScreenCommandRequest(int PublishedRevision, Guid? RequestId = null, bool Confirmed = false, double? Value = null, string? Operation = null, string? Phase = null);
public sealed record ScreenThumbnailNode(string Id, string Type, double X, double Y, double Width, double Height, double Rotation, string Color, string? Text, IReadOnlyList<ScreenPoint>? Points);
public sealed record ScreenThumbnail(double Width, double Height, string Background, bool Truncated, IReadOnlyList<ScreenThumbnailNode> Nodes);

public static class ScreenEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapScreenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/screens").RequireAuthorization();

        group.MapGet("", async (bool? draft, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, IOptions<ScadaSettings> settings, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            if (draft == true && !access.HasAny(Permission.Configure)) return Results.Json(new { message = "需要至少一个区域的配置权限，请联系管理员授权" }, statusCode: StatusCodes.Status403Forbidden);
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.ProcessScreens.AsNoTracking();
            if (draft != true)
            {
                query = query.Where(x => x.PublishedJson != null);
                // 可选治理策略：运行画面目录只显示账号可见区域的画面；跨区域画面始终可见。
                if (settings.Value.FilterPublishedScreensByArea && access.AreasWith(Permission.View) is { } visible)
                {
                    var areaIds = visible.ToArray();
                    query = query.Where(x => x.AreaId == null || areaIds.Contains(x.AreaId.Value));
                }
            }
            else if (!access.HasGlobal(Permission.Configure))
            {
                var areas = (access.AreasWith(Permission.Configure) ?? new HashSet<int>()).ToList();
                query = query.Where(x => x.PublishedJson != null || x.AreaId != null && areas.Contains(x.AreaId.Value));
            }
            var screens = await query.OrderBy(x => x.Id).Select(x => new
            {
                x.Id, x.AreaId, Name = draft == true ? x.Name : x.PublishedName,
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
                x.Id, x.AreaId, x.Name, x.Revision, x.PublishedRevision, x.UpdatedTs, x.UpdatedBy, x.HasDraftChanges,
                LayoutCount = layoutCounts.GetValueOrDefault(x.Id),
                LayoutNames = layoutLabels.TryGetValue(x.Id, out var names) ? names.ToArray() : Array.Empty<string>()
            }));
        });

        group.MapGet("/{id:int}", async (int id, bool? draft, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var screen = await db.ProcessScreens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null || draft != true && screen.PublishedJson is null) return Results.NotFound();
            if (draft == true && !Screens.ScreenAreaRules.CanEdit(access, screen.AreaId)) return Denied();
            var document = JsonSerializer.Deserialize<ScreenDocument>(draft == true ? screen.DraftJson : screen.PublishedJson!, Json)!;
            var requiresRepublish = draft != true && Screens.ScreenPublication.RequiresRepublish(document);
            return Results.Ok(new
            {
                screen.Id, screen.AreaId, Name = draft == true ? screen.Name : screen.PublishedName,
                Revision = draft == true ? screen.Revision : screen.PublishedRevision,
                screen.PublishedRevision, screen.UpdatedTs, screen.UpdatedBy,
                Document = document, RequiresRepublish = requiresRepublish,
                Message = requiresRepublish ? Screens.ScreenPublication.RepublishMessage : null
            });
        });

        group.MapGet("/{id:int}/thumbnail", async (int id, bool? draft, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var screen = await db.ProcessScreens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null || draft != true && screen.PublishedJson is null) return Results.NotFound();
            if (draft == true && !Screens.ScreenAreaRules.CanEdit(access, screen.AreaId)) return Denied();
            ScreenDocument? document;
            try { document = JsonSerializer.Deserialize<ScreenDocument>(draft == true ? screen.DraftJson : screen.PublishedJson!, Json); }
            catch (JsonException) { return EndpointHelpers.BadRequest("画面内容无法解析"); }
            if (document is null) return EndpointHelpers.BadRequest("画面内容不能为空");
            var source = (document.Nodes ?? []).Where(node => node is not null && node.Hidden != true).ToList();
            var nodes = source.Take(400).Select(node => new ScreenThumbnailNode(
                node.Id ?? "", node.Type ?? "", node.X, node.Y, node.Width, node.Height, node.Rotation,
                node.Color ?? "#7394b8",
                // 图片 data URI 可能接近 700 KB，缩略图只画占位框，不回传整段内容。
                node.Type == "image" ? null : ShortThumbnailText(node.Text),
                node.Type == "polyline" ? node.Points : null)).ToList();
            return Results.Ok(new ScreenThumbnail(document.Width, document.Height, document.Background ?? "#101b2d", source.Count > 400, nodes));
        });

        group.MapPost("", async (ScreenCreateRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ScreenCommandService screens, CancellationToken ct) =>
        {
            if (!Screens.ScreenAreaRules.CanEdit(await http.GetAccessAsync(), request.AreaId)) return Denied();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            return MapWrite(await screens.CreateAsync(db, runtime, request.Name, request.AreaId, http.User.GetUserName(), http.User.GetUserId(), ct), created: true);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (int id, ScreenSaveRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ScreenCommandService screens, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var current = await db.ProcessScreens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (current is null) return Results.NotFound();
            var nextArea = request.UpdateArea ? request.AreaId : current.AreaId;
            if (!Screens.ScreenAreaRules.CanEdit(access, current.AreaId) || !Screens.ScreenAreaRules.CanEdit(access, nextArea)) return Denied();
            return MapWrite(await screens.SaveDraftAsync(db, runtime, id, request.Name, request.Revision, request.Document, request.AreaId, request.UpdateArea, http.User.GetUserName(), http.User.GetUserId(), ct));
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/validate", async (ScreenDocument document, int? screenId, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var (issues, warnings) = await ValidateReferencesAsync(db, document, screenId, ct);
            return Results.Ok(new { issues, warnings });
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapGet("/{id:int}/versions", async (int id, int? limit, int? beforeRevision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (beforeRevision is <= 0) return EndpointHelpers.BadRequest("版本游标须为正整数");
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await GuardAsync(http, db, id, ct) is { } denied) return denied;
            var query = db.ProcessScreenVersions.AsNoTracking().Where(x => x.ScreenId == id);
            if (beforeRevision is int before) query = query.Where(x => x.Revision < before);
            return Results.Ok(await query.OrderByDescending(x => x.Revision).Take(Math.Clamp(limit ?? 50, 1, 200))
                .Select(x => new { x.Revision, x.Name, x.PublishedTs, x.PublishedBy, x.Note, x.Kind, x.RestoredFromRevision }).ToListAsync(ct));
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapGet("/{id:int}/versions/{revision:int}", async (int id, int revision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await GuardAsync(http, db, id, ct) is { } denied) return denied;
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
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapGet("/{id:int}/usage", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await GuardAsync(http, db, id, ct) is { } denied) return denied;
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
        }).RequireAuthorization(AccessPolicies.Configure);
        group.MapPost("/{id:int}/publish", (int id, ScreenRevisionRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ScreenCommandService screens, CancellationToken ct) =>
            PublishAsync(id, request.Revision, null, request.Note, http, factory, runtimeFactory, screens, ct)).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/{id:int}/rollback", (int id, ScreenRollbackRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ScreenCommandService screens, CancellationToken ct) =>
            PublishAsync(id, request.Revision, request.TargetRevision, request.Note, http, factory, runtimeFactory, screens, ct)).RequireAuthorization(AccessPolicies.Configure);

        // 运行端异常上报：只写健康摘要与审计，不改变画面配置；同一画面同类异常由服务端节流。
        group.MapPost("/{id:int}/runtime-report", async (int id, ScreenRuntimeReportRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ScreenRuntimeHealth health, CancellationToken ct) =>
        {
            var kind = request.Kind?.Trim() ?? "";
            if (!ScreenRuntimeHealth.Kinds.Contains(kind)) return EndpointHelpers.BadRequest("运行端上报类型无效");
            var message = (request.Message ?? "").Trim();
            if (message.Length > 500) return EndpointHelpers.BadRequest("上报内容最多 500 个字符");
            await using var db = await factory.CreateDbContextAsync(ct);
            var screen = await db.ProcessScreens.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Name, x.PublishedName, x.AreaId, x.PublishedJson })
                .FirstOrDefaultAsync(ct);
            if (screen is null) return Results.NotFound();
            var access = await http.GetAccessAsync();
            if (screen.PublishedJson is null && !Screens.ScreenAreaRules.CanEdit(access, screen.AreaId)) return Results.NotFound();
            var name = string.IsNullOrWhiteSpace(screen.PublishedName) ? screen.Name : screen.PublishedName;
            var now = ScadaTime.NowMs();
            if (!health.Report(id, name, kind, message, now)) return Results.NoContent();
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            runtime.OperationLogs.Add(new OperationLog
            {
                UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = now,
                Action = $"运行端异常：{name}（{kind}）", Result = "失败",
                TargetType = "ScreenRuntime", TargetId = id.ToString(),
                Details = JsonSerializer.Serialize(new { kind, message, request.PublishedRevision })
            });
            await runtime.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapDelete("/{id:int}", async (int id, int revision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var screen = await db.ProcessScreens.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (screen.Revision != revision) return Conflict();
            if (!Screens.ScreenAreaRules.CanEdit(await http.GetAccessAsync(), screen.AreaId)) return Denied();
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
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/{id:int}/actions/{nodeId}", (int id, string nodeId, ScreenCommandRequest request,
            HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ControlCommandService commands, MomentaryControlService momentary, CancellationToken ct) =>
            Screens.ScreenActions.ExecuteAsync(id, nodeId, request, http, factory, commands, momentary, ct)).RequireAuthorization(AccessPolicies.Control);
    }

    private static async Task<IResult> PublishAsync(int id, int revision, int? targetRevision, string? note, HttpContext http,
        IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, ScreenCommandService screens, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var current = await db.ProcessScreens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (current is null) return Results.NotFound();
        if (!Screens.ScreenAreaRules.CanEdit(await http.GetAccessAsync(), current.AreaId)) return Denied();
        var result = await screens.PublishAsync(db, runtime, id, revision, targetRevision, note, http.User.GetUserName(), http.User.GetUserId(), ct);
        if (result.Success)
        {
            var warning = result.SnapshotFailed
                ? "运行画面已更新，但自动创建配置快照失败，请在工程管理中手动创建快照；详情见系统告警"
                : (string?)null;
            return Results.Ok(new { result.Screen!.Id, result.Screen.Revision, result.Screen.PublishedRevision, versionId = result.ConfigVersionId, warning, warnings = result.Warnings });
        }
        return MapWrite(result);
    }

    private static string? ShortThumbnailText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= 40 ? text : text[..40];
    private static IResult MapWrite(ScreenCommandResult result, bool created = false)
    {
        if (result.Success)
        {
            var body = new { result.Screen!.Id, result.Screen.Revision, result.Screen.AreaId };
            return created ? Results.Created($"/api/screens/{result.Screen.Id}", body) : Results.Ok(body);
        }
        return result.Error switch
        {
            ScreenCommandError.InvalidName => EndpointHelpers.BadRequest("画面名称须为 1～100 个字符"),
            ScreenCommandError.InvalidNote => EndpointHelpers.BadRequest(result.Message ?? "说明无效"),
            ScreenCommandError.InvalidDocument => Results.BadRequest(new { message = "画面格式无效", issues = result.Issues }),
            ScreenCommandError.EmptyDocument => EndpointHelpers.BadRequest("画面内容不能为空"),
            ScreenCommandError.Unreadable => EndpointHelpers.BadRequest("画面内容无法解析"),
            ScreenCommandError.AreaMissing => EndpointHelpers.BadRequest("区域不存在"),
            ScreenCommandError.AreaMismatch => EndpointHelpers.BadRequest(result.Message ?? "画面绑定了其他区域的测点"),
            ScreenCommandError.NotFound or ScreenCommandError.VersionNotFound => Results.NotFound(),
            ScreenCommandError.Conflict => Conflict(),
            ScreenCommandError.ValidationFailed => Results.BadRequest(new { message = result.Message ?? "发布校验未通过", issues = result.Issues, warnings = result.Warnings }),
            _ => EndpointHelpers.BadRequest(result.Message ?? "画面写入失败")
        };
    }

    private static IResult Denied() => Results.Json(new { message = "需要该画面所属区域的配置权限" }, statusCode: StatusCodes.Status403Forbidden);
    private static async Task<IResult?> GuardAsync(HttpContext http, ConfigDbContext db, int id, CancellationToken ct)
    {
        var screen = await db.ProcessScreens.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.AreaId }).FirstOrDefaultAsync(ct);
        if (screen is null) return Results.NotFound();
        return Screens.ScreenAreaRules.CanEdit(await http.GetAccessAsync(), screen.AreaId) ? null : Denied();
    }
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
    internal static async Task<(IReadOnlyList<ScreenIssue> Issues, IReadOnlyList<ScreenIssue> Warnings)> ValidateReferencesAsync(ConfigDbContext db, ScreenDocument document, int? selfId, CancellationToken ct)
    {
        var publication = await Screens.ScreenPublication.PrepareAsync(db, document, selfId, ct);
        return (publication.Issues, publication.Warnings);
    }
    private static void AuditAction(RuntimeDbContext runtime, HttpContext http, string action) => runtime.OperationLogs.Add(new OperationLog
    {
        UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(), Action = action, Result = "成功"
    });
}
