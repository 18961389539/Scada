using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Templates;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record ObjectTemplateSaveRequest(string Name, int Revision, ObjectTemplateDocument Document);
public sealed record ObjectTemplatePublishRequest(int Revision);

public static class ObjectTemplateEndpoints
{
    public static void MapObjectTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var templates = app.MapGroup("/api/object-templates").RequireAuthorization("Admin");
        templates.MapGet("/examples/pump", () => Results.Ok(ObjectTemplateExamples.Pump()));
        templates.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(await db.ObjectTemplates.AsNoTracking().OrderBy(x => x.Id).Select(x => new
            { x.Id, x.Name, x.Revision, x.LatestVersion, x.UpdatedTs, x.UpdatedBy, InstanceCount = db.ObjectInstances.Count(i => i.TemplateId == x.Id) }).ToListAsync(ct));
        });
        templates.MapGet("/{id:int}", async (int id, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.ObjectTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            var versions = await db.ObjectTemplateVersions.AsNoTracking().Where(x => x.TemplateId == id).OrderByDescending(x => x.Version)
                .Select(x => new { x.Version, x.CreatedTs, x.CreatedBy }).ToListAsync(ct);
            return Results.Ok(new { row.Id, row.Name, row.Revision, row.LatestVersion, Document = ObjectTemplateJson.Read<ObjectTemplateDocument>(row.DraftJson), Versions = versions });
        });
        templates.MapGet("/{id:int}/versions/{version:int}", async (int id, int version, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.ObjectTemplateVersions.AsNoTracking().FirstOrDefaultAsync(x => x.TemplateId == id && x.Version == version, ct);
            return row is null ? Results.NotFound() : Results.Ok(new { row.Version, Document = ObjectTemplateJson.Read<ObjectTemplateDocument>(row.DocumentJson) });
        });
        templates.MapPost("/validate", (ObjectTemplateDocument document) => Results.Ok(new { errors = ObjectTemplateValidator.Validate(document) }));
        templates.MapPost("", async (ObjectTemplateSaveRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0) return Results.BadRequest(new { message = "模板定义无效", errors });
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = new DeviceObjectTemplate { Name = request.Name.Trim(), DraftJson = ObjectTemplateJson.Write(request.Document), UpdatedTs = ScadaTime.NowMs(), UpdatedBy = http.User.GetUserName() };
            db.ObjectTemplates.Add(row); Audit(db, http, $"创建对象模板 {row.Name}");
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { return Collision(); }
            return Results.Created($"/api/object-templates/{row.Id}", new { row.Id, row.Revision });
        });
        templates.MapPut("/{id:int}", async (int id, ObjectTemplateSaveRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var errors = Validate(request);
            if (errors.Count > 0) return Results.BadRequest(new { message = "模板定义无效", errors });
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.ObjectTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (row.Revision != request.Revision) return Collision();
            row.Name = request.Name.Trim(); row.DraftJson = ObjectTemplateJson.Write(request.Document); row.Revision++;
            row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName(); Audit(db, http, $"保存对象模板 {row.Name} r{row.Revision}");
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { return Collision(); }
            return Results.Ok(new { row.Id, row.Revision });
        });
        templates.MapPost("/{id:int}/publish", async (int id, ObjectTemplatePublishRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.ObjectTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (row.Revision != request.Revision) return Collision();
            var doc = ObjectTemplateJson.Read<ObjectTemplateDocument>(row.DraftJson);
            var errors = ObjectTemplateValidator.Validate(doc).ToList();
            var targets = await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(ct);
            if (doc.Panel.Nodes.Any(n => n.Action.Type is "navigate" or "popup" && !targets.Contains(n.Action.ScreenId ?? 0))) errors.Add("面板导航目标不存在或未发布");
            if (errors.Count > 0) return Results.BadRequest(new { message = "模板发布校验未通过", errors });
            row.LatestVersion++; row.Revision++; row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
            db.ObjectTemplateVersions.Add(new DeviceObjectTemplateVersion { TemplateId = id, Version = row.LatestVersion, DocumentJson = row.DraftJson, CreatedTs = row.UpdatedTs, CreatedBy = row.UpdatedBy });
            Audit(db, http, $"发布对象模板 {row.Name} v{row.LatestVersion}");
            try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); } catch (DbUpdateException) { return Collision(); }
            return Results.Ok(new { row.Id, row.Revision, row.LatestVersion });
        });
        templates.MapDelete("/{id:int}", async (int id, int revision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.ObjectTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (row.Revision != revision) return Collision();
            if (await db.ObjectInstances.AnyAsync(x => x.TemplateId == id, ct)) return EndpointHelpers.BadRequest("仍有实例使用此模板，请先解除关联");
            db.ObjectTemplates.Remove(row); Audit(db, http, $"删除对象模板 {row.Name}");
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { return Collision(); }
            return Results.NoContent();
        });
        templates.MapPost("/{id:int}/instantiate/preview", (int id, ObjectBatchRequest request, ObjectTemplateService service, CancellationToken ct) =>
            Run(async () => await service.PreviewCreateAsync(id, request, ct)));
        templates.MapPost("/{id:int}/instantiate", (int id, ObjectBatchRequest request, HttpContext http, ObjectTemplateService service, ConfigReloader reloader, CancellationToken ct) =>
            Run(async () => { var result = await service.CreateAsync(id, request, http.User.GetUserId(), http.User.GetUserName(), ct); await reloader.ReloadAsync(ct); return result; }));

        var instances = app.MapGroup("/api/object-instances").RequireAuthorization("Admin");
        instances.MapGet("", async (int? templateId, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(await (from i in db.ObjectInstances.AsNoTracking()
                join t in db.ObjectTemplates on i.TemplateId equals t.Id
                join d in db.Devices on i.DeviceId equals d.Id
                where templateId == null || i.TemplateId == templateId
                orderby i.Id
                select new { i.Id, i.Name, i.TemplateId, TemplateName = t.Name, i.AppliedVersion, t.LatestVersion, i.DeviceId, DeviceName = d.Name, i.BaseAddress, i.ScreenId, i.Revision, i.UpdatedTs }).ToListAsync(ct));
        });
        instances.MapGet("/{id:int}", async (int id, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.ObjectInstances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return row is null ? Results.NotFound() : Results.Ok(new { row.Id, row.Name, row.TemplateId, row.AppliedVersion, row.DeviceId, row.BaseAddress, row.ScreenId, row.Revision, Baseline = ObjectTemplateJson.Read<ObjectInstanceBaseline>(row.BaselineJson) });
        });
        instances.MapPost("/{id:int}/upgrade/preview", (int id, ObjectUpgradeRequest request, ObjectTemplateService service, CancellationToken ct) => Run(async () => await service.PreviewUpgradeAsync(id, request, ct)));
        instances.MapPost("/upgrade", (ObjectUpgradeBatchRequest request, HttpContext http, ObjectTemplateService service, ConfigReloader reloader, CancellationToken ct) =>
            Run(async () => { var result = await service.UpgradeAsync(request, http.User.GetUserId(), http.User.GetUserName(), ct); await reloader.ReloadAsync(ct); return result; }));
        instances.MapDelete("/{id:int}", async (int id, int revision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.ObjectInstances.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (row.Revision != revision) return Collision();
            db.ObjectInstances.Remove(row); Audit(db, http, $"解除对象模板关联 {row.Name}，保留测点、报警及面板");
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { return Collision(); }
            return Results.NoContent();
        });
    }
    private static IReadOnlyList<string> Validate(ObjectTemplateSaveRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100) return ["模板名称须为 1～100 个字符"];
        return ObjectTemplateValidator.Validate(request.Document, false);
    }
    private static IResult Collision() => Results.Conflict(new { message = "名称冲突或版本已变化，请重新打开并预览" });
    private static async Task<IResult> Run(Func<Task<object>> action)
    {
        try { return Results.Ok(await action()); }
        catch (TemplateOperationException ex) { return Results.Json(new { message = ex.Message, details = ex.Details }, statusCode: ex.Status); }
        catch (DbUpdateException) { return Collision(); }
    }
    private static void Audit(ConfigDbContext db, HttpContext http, string action) => db.OperationLogs.Add(new OperationLog { UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(), Action = action, Result = "成功" });
}
