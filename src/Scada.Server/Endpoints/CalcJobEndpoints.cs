using Microsoft.EntityFrameworkCore;
using Scada.Core.Calculations;
using Scada.Core.Entities;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

// 计算任务（累计器/运行小时/能耗/受限脚本）。区域规则：
// - 查看：任务所在区域的 View；
// - 新建/修改/删除：任务区域（修改时新旧区域）的 Configure，且能查看全部输入/可读测点；
// - 脚本可写测点：保存人须对每个可写测点有 Control（运行时还会以保存人身份复核）；
// - 手动复位、维护确认：任务区域的 Control。全部写操作记差分审计，拒绝也记审计。
public static class CalcJobEndpoints
{
    public static void MapCalcJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/calc-jobs").RequireAuthorization();
        group.MapGet("", (CalcJobConfiguration service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            var access = await http.GetAccessAsync();
            return Results.Ok((await service.ListAsync(ct)).Where(x => access.Has(Permission.View, x.Job.AreaId)).ToList());
        }));
        group.MapGet("/{id:int}/snapshots", (int id, int? take, CalcJobConfiguration service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            var job = await service.GetAsync(id, ct); if (job is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.View, job.AreaId)) return Results.Forbid();
            return Results.Ok(await service.SnapshotsAsync(id, take ?? 100, ct));
        }));
        group.MapPost("", (CalcJobRequest request, CalcJobConfiguration service, ConfigReloader reloader, IDbContextFactory<ConfigDbContext> factory, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            if (await CheckAsync(http, service, null, request, $"创建计算任务：{request.Name}", ct) is { } denied) return denied;
            var job = await service.SaveAsync(null, request, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            await reloader.ReloadAsync(CancellationToken.None); return Results.Created($"/api/calc-jobs/{job.Id}", job);
        })).RequireAuthorization(AccessPolicies.Configure);
        group.MapPut("/{id:int}", (int id, CalcJobRequest request, CalcJobConfiguration service, ConfigReloader reloader, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            if (await CheckAsync(http, service, id, request, $"修改计算任务 #{id}", ct) is { } denied) return denied;
            var job = await service.SaveAsync(id, request, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            await reloader.ReloadAsync(CancellationToken.None); return Results.Ok(job);
        })).RequireAuthorization(AccessPolicies.Configure);
        group.MapDelete("/{id:int}", (int id, long revision, CalcJobConfiguration service, ConfigReloader reloader, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            var job = await service.GetAsync(id, ct); if (job is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Configure, job.AreaId))
                return await Audit.ForbidAsync(http, $"删除计算任务 #{id}", "无权配置该区域的计算任务", "CalcJob", id);
            await service.DeleteAsync(id, revision, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            await reloader.ReloadAsync(CancellationToken.None); return Results.NoContent();
        })).RequireAuthorization(AccessPolicies.Configure);
        group.MapPost("/{id:int}/reset", (int id, CalcJobConfiguration service, CalcJobEngine engine, HttpContext http, CancellationToken ct) =>
            Operate(http, service, id, "手动复位计算任务", job => engine.ResetAsync(job.Id, "手动复位", ct), ct)).RequireAuthorization(AccessPolicies.Control);
        group.MapPost("/{id:int}/maintenance-done", (int id, CalcJobConfiguration service, CalcJobEngine engine, HttpContext http, CancellationToken ct) =>
            Operate(http, service, id, "确认设备维护", job => engine.MaintenanceDoneAsync(job.Id, ct), ct)).RequireAuthorization(AccessPolicies.Control);
    }

    private static Task<IResult> Operate(HttpContext http, CalcJobConfiguration service, int id, string action, Func<CalcJob, Task<bool>> operation, CancellationToken ct) => Run(http, async () =>
    {
        var job = await service.GetAsync(id, ct); if (job is null) return Results.NotFound();
        if (!(await http.GetAccessAsync()).Has(Permission.Control, job.AreaId))
            return await Audit.ForbidAsync(http, $"{action} #{id}", "无权操作该区域的计算任务", "CalcJob", id);
        if (!await operation(job)) return EndpointHelpers.BadRequest("任务未运行或类型不支持该操作");
        await service.AuditAsync(job, action, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
        return Results.NoContent();
    });

    private static async Task<IResult?> CheckAsync(HttpContext http, CalcJobConfiguration service, int? id, CalcJobRequest request, string action, CancellationToken ct)
    {
        var access = await http.GetAccessAsync();
        if (id is int existing)
        {
            var current = await service.GetAsync(existing, ct);
            if (current is null) return Results.NotFound();
            if (!access.Has(Permission.Configure, current.AreaId))
                return await Audit.ForbidAsync(http, action, "无权配置该区域的计算任务", "CalcJob", existing);
        }
        if (!access.Has(Permission.Configure, request.AreaId))
            return await Audit.ForbidAsync(http, action, $"无权配置区域 #{request.AreaId}", "Area", request.AreaId);
        IReadOnlySet<int> reads, writes;
        try
        {
            reads = request.Kind == CalcJobKind.Script ? CalcScript.ParseIds(request.ReadTags ?? "") : request.InputTagId is int input ? new HashSet<int> { input } : new HashSet<int>();
            writes = request.Kind == CalcJobKind.Script ? CalcScript.ParseIds(request.WriteTags ?? "") : new HashSet<int>();
        }
        catch (TagExpressionException) { return null; } // 交给服务层返回 400
        var hidden = reads.Where(x => !access.CanTag(Permission.View, x)).ToArray();
        if (hidden.Length > 0) return await Audit.ForbidAsync(http, action, $"引用了无权查看的测点：{string.Join(",", hidden)}", "CalcJob", id);
        var uncontrolled = writes.Where(x => !access.CanTag(Permission.Control, x)).ToArray();
        if (uncontrolled.Length > 0) return await Audit.ForbidAsync(http, action, $"无权控制可写测点：{string.Join(",", uncontrolled)}", "CalcJob", id);
        return null;
    }

    private static async Task<IResult> Run(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (CalculationRequestException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.Status); }
        catch (TagExpressionException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        catch (DbUpdateException) { return Results.Json(new { message = "名称、引用或版本已变化，请重新读取后核对" }, statusCode: 409); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return Results.Problem("配置数据库暂不可用，请核对实际状态后重试", statusCode: 503); }
    }
}
