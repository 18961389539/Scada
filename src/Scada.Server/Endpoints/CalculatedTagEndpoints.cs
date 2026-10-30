using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public static class CalculatedTagEndpoints
{
    public static void MapCalculatedTagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/calculated-tags").RequireAuthorization();
        group.MapGet("", (CalculatedTagConfiguration service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            var access = await http.GetAccessAsync();
            return Results.Ok((await service.ListAsync(ct)).Where(x => access.CanTag(Permission.View, x.Tag.Id)).ToList());
        }));
        group.MapPost("/preview", (int? id, CalculatedTagRequest request, CalculatedTagConfiguration service, IDbContextFactory<ConfigDbContext> factory, HttpContext http, CancellationToken ct) =>
            Run(http, async () =>
            {
                if (await CheckAsync(http, factory, id, request, "预览计算测点", ct) is { } denied) return denied;
                return Results.Ok(await service.PreviewAsync(id, request, ct));
            })).RequireAuthorization(AccessPolicies.Configure);
        group.MapPost("", (CalculatedTagRequest request, CalculatedTagConfiguration service, ConfigReloader reloader, IDbContextFactory<ConfigDbContext> factory, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            if (await CheckAsync(http, factory, null, request, $"创建计算测点：{request.Name}", ct) is { } denied) return denied;
            var tag = await service.SaveAsync(null, request, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            await reloader.ReloadAsync(CancellationToken.None); return Results.Created($"/api/tags/{tag.Id}", tag);
        })).RequireAuthorization(AccessPolicies.Configure);
        group.MapPut("/{id:int}", (int id, CalculatedTagRequest request, CalculatedTagConfiguration service, ConfigReloader reloader, IDbContextFactory<ConfigDbContext> factory, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            if (await CheckAsync(http, factory, id, request, $"修改计算测点 #{id}", ct) is { } denied) return denied;
            var tag = await service.SaveAsync(id, request, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            await reloader.ReloadAsync(CancellationToken.None); return Results.Ok(tag);
        })).RequireAuthorization(AccessPolicies.Configure);
        group.MapDelete("/{id:int}", (int id, long revision, CalculatedTagConfiguration service, ConfigReloader reloader, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            if (!(await http.GetAccessAsync()).CanTag(Permission.Configure, id))
                return await Audit.ForbidAsync(http, $"删除计算测点 #{id}", "无权配置该区域的计算测点", "CalculatedTag", id);
            await service.DeleteAsync(id, revision, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            await reloader.ReloadAsync(CancellationToken.None); return Results.NoContent();
        })).RequireAuthorization(AccessPolicies.Configure);
    }

    // 计算测点区域规则：显式 AreaId 时需该区域的配置权；未指定时需全部输入测点所在区域的配置权。
    // 无论哪种，都必须能查看全部输入测点，防止借计算测点读出无权区域的数据。
    private static async Task<IResult?> CheckAsync(HttpContext http, IDbContextFactory<ConfigDbContext> factory, int? id, CalculatedTagRequest request, string action, CancellationToken ct)
    {
        var access = await http.GetAccessAsync();
        if (id is int existing && !access.CanTag(Permission.Configure, existing))
            return await Audit.ForbidAsync(http, action, "无权配置该区域的计算测点", "CalculatedTag", existing);
        IReadOnlyList<int> inputs;
        try { inputs = TagExpression.Compile(request.Expression ?? "").Dependencies; }
        catch (TagExpressionException) { return null; } // 表达式错误交给服务层返回 400
        var hidden = inputs.Where(x => !access.CanTag(Permission.View, x)).ToArray();
        if (hidden.Length > 0)
            return await Audit.ForbidAsync(http, action, $"表达式引用了无权查看的测点：{string.Join(",", hidden)}", "CalculatedTag", id);
        if (request.AreaId is int areaId and > 0)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.Areas.AnyAsync(x => x.Id == areaId, ct)) return EndpointHelpers.BadRequest("区域不存在");
            if (!access.Has(Permission.Configure, areaId))
                return await Audit.ForbidAsync(http, action, $"无权配置区域 #{areaId}", "Area", areaId);
            return null;
        }

        var areas = inputs.SelectMany(x => access.Model.AreasOfTag(x)).Distinct().ToArray();
        if (areas.Length == 0) areas = [Area.DefaultId];
        return access.HasAll(Permission.Configure, areas) ? null
            : await Audit.ForbidAsync(http, action, "未指定区域的计算测点需要全部输入区域的配置权限", "CalculatedTag", id);
    }

    private static async Task<IResult> Run(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (CalculationRequestException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.Status); }
        catch (TagExpressionException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        catch (DbUpdateException) { return Results.Json(new { message = "计算名称、引用或版本已变化，请重新读取后核对" }, statusCode: 409); }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            EndpointHelpers.LogFailure(http, "Scada.Server.Endpoints.CalculatedTagEndpoints", ex);
            return Results.Problem("配置数据库暂不可用，请核对实际状态后重试", statusCode: 503);
        }
    }
}
