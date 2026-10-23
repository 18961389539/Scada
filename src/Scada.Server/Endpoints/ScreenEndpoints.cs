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
public sealed record ScreenRevisionRequest(int Revision);
public sealed record ScreenCommandRequest(int PublishedRevision);

public static class ScreenEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapScreenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/screens").RequireAuthorization();

        group.MapGet("", async (bool? draft, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (draft == true && !http.User.IsInRole("Admin")) return Results.Forbid();
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.ProcessScreens.AsNoTracking();
            if (draft != true) query = query.Where(x => x.PublishedJson != null);
            var screens = await query.OrderBy(x => x.Id).ToListAsync(ct);
            return Results.Ok(screens.Select(x => new
            {
                x.Id, Name = draft == true ? x.Name : x.PublishedName,
                Revision = draft == true ? x.Revision : x.PublishedRevision,
                x.PublishedRevision, x.UpdatedTs, x.UpdatedBy,
                HasDraftChanges = draft == true && x.Revision != x.PublishedRevision
            }));
        });

        group.MapGet("/{id:int}", async (int id, bool? draft, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (draft == true && !http.User.IsInRole("Admin")) return Results.Forbid();
            await using var db = await factory.CreateDbContextAsync(ct);
            var screen = await db.ProcessScreens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null || draft != true && screen.PublishedJson is null) return Results.NotFound();
            return Results.Ok(new
            {
                screen.Id, Name = draft == true ? screen.Name : screen.PublishedName,
                Revision = draft == true ? screen.Revision : screen.PublishedRevision,
                screen.PublishedRevision, screen.UpdatedTs, screen.UpdatedBy,
                Document = JsonSerializer.Deserialize<ScreenDocument>(draft == true ? screen.DraftJson : screen.PublishedJson!, Json)
            });
        });

        group.MapPost("", async (ScreenCreateRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (!ValidName(request.Name)) return EndpointHelpers.BadRequest("画面名称须为 1～100 个字符");
            await using var db = await factory.CreateDbContextAsync(ct);
            var screen = new ProcessScreen
            {
                Name = request.Name.Trim(), DraftJson = JsonSerializer.Serialize(new ScreenDocument(), Json),
                UpdatedTs = ScadaTime.NowMs(), UpdatedBy = http.User.GetUserName()
            };
            db.ProcessScreens.Add(screen);
            Audit(db, http, $"创建画面 {screen.Name}");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/screens/{screen.Id}", new { screen.Id, screen.Revision });
        }).RequireAuthorization("Admin");

        group.MapPut("/{id:int}", async (int id, ScreenSaveRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (!ValidName(request.Name) || request.Document is null) return EndpointHelpers.BadRequest("画面名称或内容无效");
            var shapeIssues = ScreenValidator.Validate(request.Document);
            if (shapeIssues.Count > 0) return Results.BadRequest(new { message = "画面格式无效", issues = shapeIssues });
            await using var db = await factory.CreateDbContextAsync(ct);
            var screen = await db.ProcessScreens.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (screen.Revision != request.Revision) return Conflict();
            screen.Name = request.Name.Trim();
            screen.DraftJson = JsonSerializer.Serialize(request.Document, Json);
            screen.Revision++;
            screen.UpdatedTs = ScadaTime.NowMs();
            screen.UpdatedBy = http.User.GetUserName();
            Audit(db, http, $"保存画面 {screen.Name} r{screen.Revision}");
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            return Results.Ok(new { screen.Id, screen.Revision });
        }).RequireAuthorization("Admin");

        group.MapPost("/validate", async (ScreenDocument document, int? screenId, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(new { issues = await ValidateReferencesAsync(db, document, screenId, ct) });
        }).RequireAuthorization("Admin");

        group.MapPost("/{id:int}/publish", async (int id, ScreenRevisionRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var screen = await db.ProcessScreens.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen is null) return Results.NotFound();
            if (screen.Revision != request.Revision) return Conflict();
            var document = JsonSerializer.Deserialize<ScreenDocument>(screen.DraftJson, Json)!;
            var issues = await ValidateReferencesAsync(db, document, id, ct);
            if (issues.Count > 0) return Results.BadRequest(new { message = "发布校验未通过", issues });
            screen.PublishedJson = screen.DraftJson;
            screen.PublishedName = screen.Name;
            // Publish advances the edit revision too, so concurrent saves and publishes cannot overwrite each other.
            screen.Revision++;
            screen.PublishedRevision = screen.Revision;
            screen.UpdatedTs = ScadaTime.NowMs();
            screen.UpdatedBy = http.User.GetUserName();
            Audit(db, http, $"发布画面 {screen.Name} r{screen.Revision}");
            try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            return Results.Ok(new { screen.Id, screen.Revision, screen.PublishedRevision });
        }).RequireAuthorization("Admin");

        group.MapDelete("/{id:int}", async (int id, int revision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
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
            db.ProcessScreens.Remove(screen);
            Audit(db, http, $"删除画面 {screen.Name}");
            try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            return Results.NoContent();
        }).RequireAuthorization("Admin");

        group.MapPost("/{id:int}/actions/{nodeId}", async (int id, string nodeId, ScreenCommandRequest request,
            HttpContext http, IDbContextFactory<ConfigDbContext> factory, ControlCommandService commands, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var screen = await db.ProcessScreens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (screen?.PublishedJson is null) return Results.NotFound();
            if (screen.PublishedRevision != request.PublishedRevision)
                return Results.Conflict(new { message = "画面已更新，请刷新后再操作" });
            var node = JsonSerializer.Deserialize<ScreenDocument>(screen.PublishedJson, Json)!.Nodes.FirstOrDefault(x => x.Id == nodeId);
            if (node?.Action.Type != "command" || node.Action.TagId is not int tagId) return EndpointHelpers.BadRequest("图元未配置控制动作");
            var result = await commands.ExecuteAsync(tagId, node.Action.Value, http.User.GetUserId(), http.User.GetUserName(), ct);
            return Results.Ok(result);
        }).RequireAuthorization("Operator");
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 100;
    private static IResult Conflict() => Results.Conflict(new { message = "画面已被其他窗口修改，请重新打开并合并修改" });
    private static async Task<IReadOnlyList<ScreenIssue>> ValidateReferencesAsync(ConfigDbContext db, ScreenDocument document, int? selfId, CancellationToken ct)
    {
        var tags = await db.Tags.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var ids = (await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        if (selfId is int id) ids.Add(id);
        return ScreenValidator.Validate(document, tags, ids);
    }
    private static void Audit(ConfigDbContext db, HttpContext http, string action) => db.OperationLogs.Add(new OperationLog
    {
        UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(), Action = action, Result = "成功"
    });
}
