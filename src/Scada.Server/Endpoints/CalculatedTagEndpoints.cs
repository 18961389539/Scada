using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Engine.Calculations;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public static class CalculatedTagEndpoints
{
    public static void MapCalculatedTagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/calculated-tags").RequireAuthorization();
        group.MapGet("", (CalculatedTagConfiguration service, HttpContext http, CancellationToken ct) => Run(http, async () => Results.Ok(await service.ListAsync(ct))));
        group.MapPost("/preview", (int? id, CalculatedTagRequest request, CalculatedTagConfiguration service, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Ok(await service.PreviewAsync(id, request, ct)))).RequireAuthorization("Admin");
        group.MapPost("", (CalculatedTagRequest request, CalculatedTagConfiguration service, ConfigReloader reloader, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            var tag = await service.SaveAsync(null, request, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            await reloader.ReloadAsync(CancellationToken.None); return Results.Created($"/api/tags/{tag.Id}", tag);
        })).RequireAuthorization("Admin");
        group.MapPut("/{id:int}", (int id, CalculatedTagRequest request, CalculatedTagConfiguration service, ConfigReloader reloader, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            var tag = await service.SaveAsync(id, request, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            await reloader.ReloadAsync(CancellationToken.None); return Results.Ok(tag);
        })).RequireAuthorization("Admin");
        group.MapDelete("/{id:int}", (int id, long revision, CalculatedTagConfiguration service, ConfigReloader reloader, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            await service.DeleteAsync(id, revision, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            await reloader.ReloadAsync(CancellationToken.None); return Results.NoContent();
        })).RequireAuthorization("Admin");
    }
    private static async Task<IResult> Run(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (CalculationRequestException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.Status); }
        catch (TagExpressionException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        catch (DbUpdateException) { return Results.Json(new { message = "计算名称、引用或版本已变化，请重新读取后核对" }, statusCode: 409); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return Results.Problem("配置数据库暂不可用，请核对实际状态后重试", statusCode: 503); }
    }
}
