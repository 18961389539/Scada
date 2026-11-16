using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record AreaRequest(string Name, string? Description);
public sealed record AccessRoleRequest(string Name, string? Description, string[] Permissions);
public sealed record AssignmentItem(int RoleId, int? AreaId);
public sealed record AssignmentsRequest(AssignmentItem[] Assignments);

public static class AccessEndpoints
{
    public sealed record AccessAreaView(int AreaId, IReadOnlyList<string> Permissions);
    public sealed record AccessView(IReadOnlyList<string> Global, IReadOnlyList<AccessAreaView> Areas);

    public static AccessView Describe(AccessContext access) => new(
        Permissions.Names(access.GlobalPermissions),
        access.AreaPermissions.OrderBy(x => x.Key).Select(x => new AccessAreaView(x.Key, Permissions.Names(x.Value))).ToArray());

    public static void MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/access", async (HttpContext http) => Results.Ok(Describe(await http.GetAccessAsync())));

        var areas = app.MapGroup("/api/areas").AddEndpointFilter<AuditFailuresFilter>();
        areas.MapGet("", async (HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var list = await db.Areas.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
            var counts = await db.Devices.AsNoTracking().GroupBy(x => x.AreaId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            return Results.Ok(list.Where(x => access.Grants.Any(g => g.AreaId is null || g.AreaId == x.Id))
                .Select(x => new { x.Id, x.Name, x.Description, deviceCount = counts.GetValueOrDefault(x.Id), isDefault = x.Id == Area.DefaultId }));
        });
        areas.MapPost("", async (AreaRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, AreaCommandService areasCommand, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return MapArea(await areasCommand.CreateAsync(db, request.Name, request.Description, ct), created: true);
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
        areas.MapPut("/{id:int}", async (int id, AreaRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, AreaCommandService areasCommand, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return MapArea(await areasCommand.UpdateAsync(db, id, request.Name, true, request.Description, true, ct));
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
        areas.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, AreaCommandService areasCommand, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var result = await areasCommand.DeleteAsync(db, id, ct);
            if (result.Success) return Results.NoContent();
            return MapArea(result);
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        var roles = app.MapGroup("/api/roles").RequireAuthorization(AccessPolicies.GlobalManageUsers).AddEndpointFilter<AuditFailuresFilter>();
        roles.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var list = await db.AccessRoles.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
            var usage = await db.UserRoleAssignments.AsNoTracking().GroupBy(x => x.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            return Results.Ok(new
            {
                permissions = Permissions.Each.Select(x => x.ToString()),
                builtIn = Enum.GetValues<Scada.Core.Enums.UserRole>().Select(r => new { role = r.ToString(), permissions = Permissions.Names(Permissions.ForBuiltIn(r)) }),
                roles = list.Select(x => new { x.Id, x.Name, x.Description, permissions = Permissions.Names(x.Permissions), assignments = usage.GetValueOrDefault(x.Id) })
            });
        });
        roles.MapPost("", async (AccessRoleRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var error = ValidateRole(request, out var name, out var permissions);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.AccessRoles.AnyAsync(x => x.Name == name, ct)) return EndpointHelpers.BadRequest("角色名称已存在");
            var role = new AccessRole { Name = name, Description = Clean(request.Description), Permissions = permissions };
            db.AccessRoles.Add(role);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/roles/{role.Id}", RoleSnapshot(role));
        });
        roles.MapPut("/{id:int}", async (int id, AccessRoleRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, AccessModelProvider provider, CancellationToken ct) =>
        {
            var error = ValidateRole(request, out var name, out var permissions);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            var role = await db.AccessRoles.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (role is null) return Results.NotFound();
            if (await db.AccessRoles.AnyAsync(x => x.Id != id && x.Name == name, ct)) return EndpointHelpers.BadRequest("角色名称已存在");
            role.Name = name; role.Description = Clean(request.Description); role.Permissions = permissions;
            await db.SaveChangesAsync(ct);
            provider.Invalidate();
            return Results.Ok(RoleSnapshot(role));
        });
        roles.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var role = await db.AccessRoles.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (role is null) return Results.NotFound();
            if (await db.UserRoleAssignments.AnyAsync(x => x.RoleId == id, ct)) return EndpointHelpers.BadRequest("角色仍分配给用户，请先解除授权");
            db.AccessRoles.Remove(role);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        var assignments = app.MapGroup("/api/users/{id:int}/assignments").RequireAuthorization(AccessPolicies.GlobalManageUsers).AddEndpointFilter<AuditFailuresFilter>();
        assignments.MapGet("", async (int id, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.Users.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            return Results.Ok(await LoadAssignmentsAsync(db, id, ct));
        });
        assignments.MapPut("", async (int id, AssignmentsRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, AccessModelProvider provider, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (user is null) return Results.NotFound();
            var items = (request.Assignments ?? []).Select(x => new AssignmentItem(x.RoleId, x.AreaId is > 0 ? x.AreaId : null)).Distinct().ToArray();
            if (items.Length > 200) return EndpointHelpers.BadRequest("单个用户最多 200 条授权");
            var roleIds = items.Select(x => x.RoleId).Distinct().ToArray();
            var areaIds = items.Where(x => x.AreaId is not null).Select(x => x.AreaId!.Value).Distinct().ToArray();
            var knownRoles = await db.AccessRoles.Where(x => roleIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            var missingRoles = roleIds.Except(knownRoles).ToArray();
            if (missingRoles.Length > 0) return EndpointHelpers.BadRequest($"角色不存在：{string.Join(",", missingRoles)}");
            var knownAreas = await db.Areas.Where(x => areaIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            var missingAreas = areaIds.Except(knownAreas).ToArray();
            if (missingAreas.Length > 0) return EndpointHelpers.BadRequest($"区域不存在：{string.Join(",", missingAreas)}");
            var before = await LoadAssignmentsAsync(db, id, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.UserRoleAssignments.Where(x => x.UserId == id).ExecuteDeleteAsync(ct);
            db.UserRoleAssignments.AddRange(items.Select(x => new UserRoleAssignment { UserId = id, RoleId = x.RoleId, AreaId = x.AreaId }));
            await db.SaveChangesAsync(ct);
            var after = await LoadAssignmentsAsync(db, id, ct);
            runtime.OperationLogs.Add(Audit.Change(http, $"修改用户授权：{user.UserName}", "UserAssignments", id,
                new { assignments = before.Select(x => $"{x.RoleName}@{x.AreaName ?? "全部区域"}").OrderBy(x => x).ToArray() },
                new { assignments = after.Select(x => $"{x.RoleName}@{x.AreaName ?? "全部区域"}").OrderBy(x => x).ToArray() }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await runtime.SaveChangesAsync(ct);
            provider.Invalidate();
            return Results.Ok(after);
        });
    }

    public sealed record AssignmentView(int Id, int RoleId, string RoleName, int? AreaId, string? AreaName, string[] Permissions);

    private static async Task<List<AssignmentView>> LoadAssignmentsAsync(ConfigDbContext db, int userId, CancellationToken ct)
    {
        var rows = await (from a in db.UserRoleAssignments.AsNoTracking()
                          join r in db.AccessRoles.AsNoTracking() on a.RoleId equals r.Id
                          join ar in db.Areas.AsNoTracking() on a.AreaId equals (int?)ar.Id into areaJoin
                          from ar in areaJoin.DefaultIfEmpty()
                          where a.UserId == userId
                          orderby a.Id
                          select new { a.Id, a.RoleId, RoleName = r.Name, a.AreaId, AreaName = ar == null ? null : ar.Name, r.Permissions }).ToListAsync(ct);
        return rows.Select(x => new AssignmentView(x.Id, x.RoleId, x.RoleName, x.AreaId, x.AreaName, Permissions.Names(x.Permissions))).ToList();
    }

    private static object RoleSnapshot(AccessRole role) => new { role.Id, role.Name, role.Description, permissions = Permissions.Names(role.Permissions) };


    private static IResult MapArea(AreaCommandResult result, bool created = false)
    {
        if (result.Success)
            return created ? Results.Created($"/api/areas/{result.Area!.Id}", result.Area) : Results.Ok(result.Area);
        return result.Error switch
        {
            AreaCommandError.NotFound => Results.NotFound(),
            _ => EndpointHelpers.BadRequest(result.Message ?? "区域写入失败")
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 500)];

    private static string? ValidateRole(AccessRoleRequest request, out string name, out Permission permissions)
    {
        name = request.Name?.Trim() ?? "";
        permissions = Permission.None;
        if (name.Length is 0 or > 100) return "角色名称须为 1～100 个字符";
        foreach (var item in request.Permissions ?? [])
        {
            if (!Enum.TryParse<Permission>(item, true, out var parsed) || !Permissions.Each.Contains(parsed)) return $"未知权限：{item}";
            permissions |= parsed;
        }

        return permissions == Permission.None ? "角色至少需要一项权限" : null;
    }
}
