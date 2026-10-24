using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Scada.Core.Models;
using Scada.Data;
using Microsoft.EntityFrameworkCore;
using Scada.Server.Auth;
using Scada.Server.Endpoints;

namespace Scada.Server.Ha;

public sealed record HaActionRequest(string? Reason, bool Force = false);

public static class HaEndpoints
{
    public static void MapHaEndpoints(this IEndpointRouteBuilder app)
    {
        // 角色摘要：登录页和顶部横幅使用，匿名可读，只含角色与主机地址。
        app.MapGet("/api/ha/role", (HaCoordinator ha) => Results.Ok(new { enabled = ha.Enabled, node = ha.Enabled ? ha.NodeId : null, role = ha.Status().Role, primaryUrl = ha.PrimaryUrl })).AllowAnonymous();

        var group = app.MapGroup("/api/ha").RequireAuthorization(AccessPolicies.GlobalSystem);
        group.MapGet("/status", (HaCoordinator ha) => Results.Ok(ha.Status()));
        group.MapPost("/switchover", (HaActionRequest request, HttpContext http, HaCoordinator ha, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
            RunAsync(http, factory, "主备手动切换", request, ct, (user, name, reason) => ha.SwitchoverAsync(user, name, reason, ct)));
        group.MapPost("/promote", (HaActionRequest request, HttpContext http, HaCoordinator ha, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
            RunAsync(http, factory, request.Force ? "主备强制接管" : "主备人工接管", request, ct, (user, name, reason) => ha.PromoteManuallyAsync(user, name, reason, request.Force, ct)));

        var peer = app.MapGroup("/api/ha/peer").AllowAnonymous().AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<HaOptions>>().Value;
            var key = context.HttpContext.Request.Headers[HaPeerClient.KeyHeader].ToString();
            if (!options.Enabled || string.IsNullOrEmpty(key) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(options.SharedKey)))
                return Results.Json(new { message = "节点密钥无效" }, statusCode: 401);
            return await next(context);
        });
        peer.MapGet("/status", (HaCoordinator ha) => Results.Ok(ha.Self()));
        peer.MapGet("/manifest", async (HaCoordinator ha, HaReplication replication, CancellationToken ct) =>
        {
            var (_, sha, bytes) = await replication.ConfigSnapshotAsync(ha.StateDir, ct);
            var self = ha.Self();
            return Results.Ok(new HaManifest(self.NodeId, self.Role, self.Term, await replication.SchemaAsync(ct), sha, bytes, replication.HistoryCursors()));
        });
        peer.MapGet("/config", async (HaCoordinator ha, HaReplication replication, CancellationToken ct) =>
        {
            var (path, sha, _) = await replication.ConfigSnapshotAsync(ha.StateDir, ct);
            // 先读到内存再返回：避免传输中被下一次快照替换。
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return Results.File(bytes, "application/octet-stream", "config.db");
        });
        peer.MapGet("/history", (string file, string table, long after, int? limit, HaReplication replication) =>
        {
            try { return Results.Ok(replication.ReadHistory(file, table, after, limit ?? 5000)); }
            catch (HaException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.Status); }
        });
        peer.MapPost("/handover", async (HaHandoverRequest request, HaCoordinator ha, CancellationToken ct) =>
        {
            try { await ha.AcceptHandoverAsync(request, ct); return Results.Ok(ha.Self()); }
            catch (Exception ex) when (ex is HaException or HttpRequestException) { return Results.Json(new { message = ex.Message }, statusCode: 409); }
        });
        peer.MapPost("/switchover", async (HaForwardRequest request, HaCoordinator ha, CancellationToken ct) =>
        {
            try { await ha.SwitchoverAsync(null, request.Actor + "（经备机）", request.Reason, ct); return Results.Ok(ha.Self()); }
            catch (HaException ex) { return Results.Json(new { message = ex.Message }, statusCode: 409); }
        });
    }

    private static async Task<IResult> RunAsync(HttpContext http, IDbContextFactory<ConfigDbContext> factory, string action, HaActionRequest request, CancellationToken ct,
        Func<int?, string, string, Task> run)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 200) return EndpointHelpers.BadRequest("请填写切换原因（200 字以内）");
        var user = http.User.GetUserId(); var name = http.User.GetUserName();
        try
        {
            await run(user, name, reason);
            return Results.Ok(new { message = "已完成" });
        }
        catch (HaException ex)
        {
            await Audit.FailedAsync(factory, http, action, ex.Message, CancellationToken.None, "HaNode");
            return Results.Json(new { message = ex.Message }, statusCode: ex.Status);
        }
    }
}

// 备机只读：拒绝网关接入与写请求，并指明主机地址；登录、主备接口和节点间接口除外。
public sealed class HaStandbyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, HaCoordinator ha)
    {
        if (ha.Enabled && !ha.IsActive)
        {
            var path = http.Request.Path;
            var gateway = path.StartsWithSegments("/api/gateway-agent");
            var write = path.StartsWithSegments("/api") && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
                && !path.StartsWithSegments("/api/auth") && !path.StartsWithSegments("/api/ha");
            if (gateway || write)
            {
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                http.Response.Headers["X-Scada-Ha-Role"] = "standby";
                if (ha.PrimaryUrl is { Length: > 0 } primary) http.Response.Headers["X-Scada-Primary"] = primary;
                http.Response.Headers.RetryAfter = "2";
                await http.Response.WriteAsJsonAsync(new { message = ha.StandbyMessage + "；备机只读", primaryUrl = ha.PrimaryUrl });
                return;
            }
        }
        await next(http);
    }
}
