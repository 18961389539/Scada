using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Projects;

namespace Scada.Server.Endpoints;

public sealed record ProjectImportRequest(ProjectPackage? Package, ConflictResolution Resolution = ConflictResolution.Skip, Dictionary<string, ConflictResolution>? Resolutions = null, string? BaseSha = null);
public sealed record ProjectSnapshotRequest(string? Note);
public sealed record ProjectRollbackRequest(string? BaseSha);

// 工程管理接口：导出/导入（预览+应用）、配置版本快照、比较、回滚。全部需要配置权限，按区域过滤与授权，变更写差分审计。
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/project").RequireAuthorization(AccessPolicies.Configure).AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("/export", async (HttpContext http, ProjectService service, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            var package = await service.ExportAsync((section, areas) => Can(access, Permission.Configure, section, areas), http.User.GetUserName(), ct);
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                db.OperationLogs.Add(Audit.Change(http, package.Partial ? "导出工程（按权限部分导出）" : "导出工程", "Project", null, null, new { summary = ProjectFormat.Summary(package), sha256 = ProjectFormat.ContentHash(package) }));
                await db.SaveChangesAsync(ct);
            }
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(package, ProjectFormat.Pretty), "application/json", $"scada-project-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        });

        group.MapPost("/import/preview", (ProjectImportRequest request, HttpContext http, ProjectService service, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
            ImportAsync(request, true, http, service, factory, ct));
        group.MapPost("/import", (ProjectImportRequest request, HttpContext http, ProjectService service, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
            ImportAsync(request, false, http, service, factory, ct));

        group.MapGet("/versions", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var list = await db.ConfigVersions.AsNoTracking().OrderByDescending(x => x.Id).Take(500)
                .Select(x => new { x.Id, x.Kind, x.Note, x.CreatedTs, x.CreatedBy, x.ContentSha256, x.SummaryJson }).ToListAsync(ct);
            return Results.Ok(list.Select(x => new { x.Id, x.Kind, x.Note, x.CreatedTs, x.CreatedBy, x.ContentSha256, summary = JsonDocument.Parse(x.SummaryJson).RootElement }));
        });
        group.MapPost("/versions", async (ProjectSnapshotRequest request, HttpContext http, ProjectService service, CancellationToken ct) =>
        {
            var note = request.Note?.Trim();
            if (string.IsNullOrEmpty(note)) return EndpointHelpers.BadRequest("请填写快照说明");
            var version = await service.SnapshotAsync("manual", note, http.User.GetUserId(), http.User.GetUserName(), ct);
            return Results.Created($"/api/project/versions/{version.Id}", new { version.Id, version.Kind, version.Note, version.CreatedTs, version.CreatedBy, version.ContentSha256 });
        });
        group.MapGet("/versions/{id:int}/download", async (int id, ProjectService service, CancellationToken ct) =>
        {
            var version = await service.GetVersionAsync(id, ct);
            return version is null ? Results.NotFound() : Results.File(System.Text.Encoding.UTF8.GetBytes(version.PackageJson), "application/json", $"scada-version-{id}.json");
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        group.MapGet("/diff", async (string from, string to, HttpContext http, ProjectService service, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            try
            {
                var a = await service.PackageOfAsync(from, ct); var b = await service.PackageOfAsync(to, ct);
                var plan = ProjectPlanner.Build(a, b, mirror: true, ConflictResolution.Overwrite);
                return Results.Ok(await ViewAsync(plan, null, http, factory, ct));
            }
            catch (ProjectException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });

        group.MapPost("/versions/{id:int}/rollback/preview", (int id, HttpContext http, ProjectService service, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
            RollbackAsync(id, null, true, http, service, factory, ct));
        group.MapPost("/versions/{id:int}/rollback", (int id, ProjectRollbackRequest request, HttpContext http, ProjectService service, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
            RollbackAsync(id, request.BaseSha, false, http, service, factory, ct));
    }

    private static bool Can(AccessContext access, Permission permission, string section, int[] areas) =>
        section == "roles" ? access.HasGlobal(Permission.ManageUsers)
        : ProjectFormat.GlobalSections.Contains(section) || areas.Length == 0 ? access.HasGlobal(permission)
        : areas.All(a => access.Has(permission, a));

    private static async Task<IResult> ImportAsync(ProjectImportRequest request, bool dryRun, HttpContext http, ProjectService service, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        if (request.Package is null) return EndpointHelpers.BadRequest("缺少工程包");
        if (!dryRun && string.IsNullOrEmpty(request.BaseSha)) return EndpointHelpers.BadRequest("请先预览再导入（缺少 baseSha）");
        try
        {
            var package = ProjectFormat.Parse(JsonSerializer.Serialize(request.Package, ProjectFormat.Json));
            var result = await service.RunAsync(package, false, request.Resolution, request.Resolutions, request.BaseSha, dryRun, await http.GetAccessAsync(), http.User.GetUserId(), http.User.GetUserName(), "导入工程并发布", ct);
            return await RespondAsync(result, dryRun, "导入工程", http, factory, ct);
        }
        catch (ProjectException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
    }

    private static async Task<IResult> RollbackAsync(int id, string? baseSha, bool dryRun, HttpContext http, ProjectService service, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        if (!dryRun && string.IsNullOrEmpty(baseSha)) return EndpointHelpers.BadRequest("请先预览影响再回滚（缺少 baseSha）");
        try
        {
            var version = await service.GetVersionAsync(id, ct);
            if (version is null) return Results.NotFound();
            var package = ProjectFormat.Parse(version.PackageJson);
            var result = await service.RunAsync(package, true, ConflictResolution.Overwrite, null, baseSha, dryRun, await http.GetAccessAsync(), http.User.GetUserId(), http.User.GetUserName(), $"回滚到配置版本 #{id}", ct);
            return await RespondAsync(result, dryRun, $"回滚到配置版本 #{id}", http, factory, ct);
        }
        catch (ProjectException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
    }

    private static async Task<IResult> RespondAsync(ProjectApplyResult result, bool dryRun, string action, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        var view = await ViewAsync(result.Plan, result, http, factory, ct);
        if (dryRun) return Results.Ok(view);
        if (result.Forbidden.Count > 0) { await Audit.DeniedAsync(factory, http, action, string.Join("；", result.Forbidden.Take(10)), ct, "Project"); return Results.Json(view, statusCode: 403); }
        if (result.Errors.Count > 0) { await Audit.FailedAsync(factory, http, action, string.Join("；", result.Errors.Take(10)), ct, "Project"); return Results.Json(view, statusCode: 400); }
        return Results.Ok(view);
    }

    // 只返回调用者有权查看的条目；其余计入 hidden。
    private static async Task<object> ViewAsync(ProjectPlan plan, ProjectApplyResult? result, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        var access = await http.GetAccessAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        var areaIds = await db.Areas.AsNoTracking().ToDictionaryAsync(x => x.Name, x => x.Id, ct);
        bool Visible(PlanEntry e)
        {
            if (e.Section == "roles") return access.HasGlobal(Permission.ManageUsers);
            if (ProjectFormat.GlobalSections.Contains(e.Section)) return access.HasGlobal(Permission.View);
            var ids = e.Areas.Select(a => areaIds.TryGetValue(a, out var id) ? id : -1).Where(x => x > 0).ToArray();
            return ids.Length == 0 ? access.HasGlobal(Permission.View) : ids.Any(a => access.Has(Permission.View, a));
        }
        var visible = plan.Entries.Where(Visible).ToList();
        return new
        {
            baseSha = result?.BaseSha, versionId = result?.VersionId,
            counts = visible.GroupBy(x => x.Action).ToDictionary(g => g.Key, g => g.Count()),
            entries = visible.Where(x => x.Action != "unchanged").Select(x => new { x.Section, sectionLabel = ProjectFormat.SectionLabels[x.Section], x.Key, x.FinalKey, x.Action, x.Areas, x.Changes }),
            hidden = plan.Entries.Count - visible.Count,
            errors = result?.Errors ?? [], forbidden = result?.Forbidden ?? [], warnings = result?.Warnings ?? plan.Warnings
        };
    }
}
