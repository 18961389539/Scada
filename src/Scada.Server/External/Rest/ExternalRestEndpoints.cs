using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Engine.Control;
using Scada.Engine.History;
using Scada.Server.Auth;
using Scada.Server.Endpoints;

namespace Scada.Server.External.Rest;

public static class ExternalRestEndpoints
{
    public const string Policy = "ExternalApi";

    public static void MapExternalRestEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1")
            .RequireAuthorization(Policy)
            .AddEndpointFilter<ExternalRateLimitFilter>()
            .AddEndpointFilter<ExternalAuditFilter>()
            .WithTags("External API v1");

        v1.MapGet("/health", (INodeRole role) => Results.Ok(new
        {
            status = "ok",
            active = role.IsActive,
            utc = DateTimeOffset.UtcNow
        })).WithSummary("健康检查");

        v1.MapGet("/tags", async (HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (await RequireScopeAsync(http, ExternalApiScope.ReadTags) is { } denied) return denied;
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var tags = await db.Tags.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
            return Results.Ok(tags.Where(t => access.CanTag(Permission.View, t.Id)).Select(TagDto).ToList());
        }).WithSummary("列出可见测点");

        v1.MapGet("/tags/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (await RequireScopeAsync(http, ExternalApiScope.ReadTags) is { } denied) return denied;
            var access = await http.GetAccessAsync();
            if (!access.CanTag(Permission.View, id)) return Results.NotFound();
            await using var db = await factory.CreateDbContextAsync(ct);
            var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return tag is null ? Results.NotFound() : Results.Ok(TagDto(tag));
        }).WithSummary("获取单个测点");

        v1.MapGet("/realtime", async (string? tagIds, HttpContext http, IRealtimeCache cache) =>
        {
            if (await RequireScopeAsync(http, ExternalApiScope.ReadTags) is { } denied) return denied;
            var access = await http.GetAccessAsync();
            var ids = EndpointHelpers.ParseIds(tagIds);
            var values = ids.Length == 0 ? cache.Snapshot().Values.OrderBy(x => x.TagId).ToList() : cache.GetMany(ids);
            return Results.Ok(values.Where(x => access.CanTag(Permission.View, x.TagId)).Select(ValueDto).ToList());
        }).WithSummary("当前值（查询参数）");

        v1.MapPost("/realtime/batch", async (RealtimeBatchRequest request, HttpContext http, IRealtimeCache cache) =>
        {
            if (await RequireScopeAsync(http, ExternalApiScope.ReadTags) is { } denied) return denied;
            if (request.TagIds is null || request.TagIds.Length == 0) return EndpointHelpers.BadRequest("请指定 tagIds");
            if (request.TagIds.Length > 500) return EndpointHelpers.BadRequest("单次最多 500 个测点");
            var access = await http.GetAccessAsync();
            var values = cache.GetMany(request.TagIds.Distinct().ToArray());
            return Results.Ok(values.Where(x => access.CanTag(Permission.View, x.TagId)).Select(ValueDto).ToList());
        }).WithSummary("当前值（批量）");

        v1.MapGet("/history", async (string? tagIds, string? from, string? to, HttpContext http, HistoryQueryService query, CancellationToken ct) =>
        {
            if (await RequireScopeAsync(http, ExternalApiScope.ReadHistory) is { } denied) return denied;
            var ids = EndpointHelpers.ParseIds(tagIds);
            if (ids.Length == 0) return EndpointHelpers.BadRequest("请指定 tagIds");
            if (ids.Length > 50) return EndpointHelpers.BadRequest("单次最多查询 50 个测点");
            if (await EndpointHelpers.RequireTagsAsync(http, ids, Permission.View, "外部历史查询") is { } areaDenied) return areaDenied;
            var toMs = EndpointHelpers.ParseTime(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fromMs = EndpointHelpers.ParseTime(from, toMs - (long)TimeSpan.FromHours(1).TotalMilliseconds);
            if (toMs < fromMs) return EndpointHelpers.BadRequest("结束时间不能早于开始时间");
            if (toMs - fromMs > (long)TimeSpan.FromDays(366).TotalMilliseconds) return EndpointHelpers.BadRequest("查询跨度不能超过一年");
            var result = await query.QueryAsync(ids, fromMs, toMs, ct);
            return Results.Ok(new { resolution = result.Resolution.ToString(), series = result.Series });
        }).WithSummary("历史查询");

        v1.MapGet("/alarms", async (string? from, string? to, bool? activeOnly, int? limit, HttpContext http, AlarmService alarms, CancellationToken ct) =>
        {
            if (await RequireScopeAsync(http, ExternalApiScope.ReadAlarms) is { } denied) return denied;
            var access = await http.GetAccessAsync();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var realtime = activeOnly == true;
            long? toMs = realtime && string.IsNullOrWhiteSpace(to) ? null : EndpointHelpers.ParseTime(to, now);
            long? fromMs = realtime && string.IsNullOrWhiteSpace(from) ? null : EndpointHelpers.ParseTime(from, (toMs ?? now) - (long)TimeSpan.FromDays(1).TotalMilliseconds);
            var take = Math.Clamp(limit ?? 200, 1, 2000);
            var fetch = access.HasGlobal(Permission.View) ? take : Math.Min(take * 4, 5000);
            var list = await alarms.QueryAsync(fromMs, toMs, activeOnly ?? false, fetch, ct, false, true, realtime);
            return Results.Ok(list.Where(x => EndpointHelpers.CanAlarm(access, Permission.View, x)).Take(take).Select(AlarmEndpoints.View).ToList());
        }).WithSummary("报警列表");

        v1.MapPost("/commands", async (ExternalCommandRequest request, HttpContext http, ControlCommandService commands, CancellationToken ct) =>
        {
            if (await RequireScopeAsync(http, ExternalApiScope.Write) is { } denied) return denied;
            if (request.TagId <= 0) return EndpointHelpers.BadRequest("请指定测点");
            if (!(await http.GetAccessAsync()).CanTag(Permission.Control, request.TagId))
                return await Audit.ForbidAsync(http, $"外部控制：测点 #{request.TagId} = {request.Value}", "无权控制该区域的测点", "Tag", request.TagId);
            var actor = new ControlActor(http.User.GetUserId(), http.User.GetUserName());
            var result = await commands.SubmitAsync(new ControlSubmission(request.TagId, request.Value, actor, ControlOrigin.Operator,
                "外部 REST API", request.Confirmed, request.RequestId), ct);
            return Results.Ok(new
            {
                success = result.Success,
                message = result.Message,
                readBack = result.ReadBack,
                commandId = result.CommandId,
                outcome = result.Outcome,
                pending = result.Pending,
                approvalId = result.ApprovalId,
                refusals = result.Refusals
            });
        }).WithSummary("下发控制（走现场控制闭环）");
    }

    public static void MapExternalAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/external").RequireAuthorization(AccessPolicies.GlobalSystem).AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("/status", (IOptionsMonitor<ExternalIntegrationOptions> opt) =>
        {
            var v = opt.CurrentValue;
            return Results.Ok(new
            {
                rest = new { v.Rest.Enabled, v.Rest.AllowJwt, cors = v.Rest.CorsOrigins, v.Rest.DefaultRateLimitPerMinute },
                tcp = new { v.Tcp.Enabled, v.Tcp.ListenAddress, v.Tcp.Port, v.Tcp.Tls, v.Tcp.MaxConnections, v.Tcp.IpAllowlist },
                opcUa = new { v.OpcUa.Enabled, v.OpcUa.EndpointUrl, v.OpcUa.SecurityModes, v.OpcUa.AllowAnonymous, v.OpcUa.AllowUsernamePassword }
            });
        });

        group.MapGet("/keys", async (ExternalApiKeyService keys, CancellationToken ct) => Results.Ok(await keys.ListAsync(ct)));

        group.MapPost("/keys", async (CreateKeyRequest request, HttpContext http, ExternalApiKeyService keys, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            try
            {
                var scopes = ParseScopes(request.Scopes);
                var created = await keys.CreateAsync(request.Name, scopes, request.AreaIds, request.RateLimitPerMinute, request.Notes, http.User.GetUserName(), ct);
                await using var db = await factory.CreateDbContextAsync(ct);
                db.OperationLogs.Add(Audit.Change(http, $"创建 API 密钥：{created.Key.Name}", "ExternalApiKey", created.Key.Id, null, new { created.Key.Name, created.Key.Scopes, created.Key.AreaIds }));
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { key = created.Key, secret = created.Secret });
            }
            catch (ArgumentException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        });

        group.MapPost("/keys/{id:int}/revoke", async (int id, HttpContext http, ExternalApiKeyService keys, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var key = await keys.RevokeAsync(id, http.User.GetUserName(), ct);
            if (key is null) return Results.NotFound();
            await using var db = await factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"吊销 API 密钥：{key.Name}", "ExternalApiKey", id, new { enabled = true }, new { enabled = false, revoked = true }));
            await db.SaveChangesAsync(ct);
            return Results.Ok(key);
        });

        group.MapPut("/keys/{id:int}/enabled", async (int id, EnableKeyRequest request, HttpContext http, ExternalApiKeyService keys, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var key = await keys.SetEnabledAsync(id, request.Enabled, ct);
            if (key is null) return Results.NotFound();
            await using var db = await factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"{(request.Enabled ? "启用" : "停用")} API 密钥：{key.Name}", "ExternalApiKey", id, null, new { request.Enabled }));
            await db.SaveChangesAsync(ct);
            return Results.Ok(key);
        });
    }

    private static async Task<IResult?> RequireScopeAsync(HttpContext http, ExternalApiScope need)
    {
        var keyScopes = http.GetExternalScopes();
        if (keyScopes is ExternalApiScope scopes)
        {
            if (!ExternalApiKeyService.HasScope(scopes, need))
                return Results.Json(new { message = $"API 密钥缺少权限：{need}" }, statusCode: StatusCodes.Status403Forbidden);
            return null;
        }
        // JWT：按用户权限映射。
        var access = await http.GetAccessAsync();
        var mapped = ExternalApiKeyService.ScopesFromUser(access);
        http.Items[ApiKeyAuthenticationHandler.ScopesItemKey] = mapped;
        if (!ExternalApiKeyService.HasScope(mapped, need))
            return Results.Json(new { message = $"当前用户缺少权限：{need}" }, statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    private static ExternalApiScope ParseScopes(string[]? names)
    {
        if (names is null || names.Length == 0) return ExternalApiScope.None;
        ExternalApiScope scopes = ExternalApiScope.None;
        foreach (var name in names)
            if (Enum.TryParse<ExternalApiScope>(name, true, out var s) && s != ExternalApiScope.All) scopes |= s;
            else if (string.Equals(name, "All", StringComparison.OrdinalIgnoreCase)) scopes = ExternalApiScope.All;
        return scopes;
    }

    private static object TagDto(Tag t) => new
    {
        t.Id, t.Name, t.DeviceId, t.Kind, t.DataType, t.Writable, t.Unit, t.Description, area = t.Area.ToString(), t.Address
    };

    private static object ValueDto(TagValue v) => new
    {
        v.TagId, v.Value, quality = v.Quality.ToString(), v.Ts, v.SourceTs, v.Error
    };

    public sealed record RealtimeBatchRequest(int[] TagIds);
    public sealed record ExternalCommandRequest(int TagId, double Value, Guid? RequestId = null, bool Confirmed = false);
    public sealed record CreateKeyRequest(string Name, string[] Scopes, int[]? AreaIds, int? RateLimitPerMinute, string? Notes);
    public sealed record EnableKeyRequest(bool Enabled);
}


public sealed class ExternalRateLimitFilter(ExternalApiKeyService keys) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Items.TryGetValue("scada.external.rateKey", out var obj) && obj is ExternalApiKey key)
        {
            if (!keys.TryAcquire(key, out var retryAfter))
            {
                http.Response.Headers.RetryAfter = retryAfter.ToString();
                return Results.Json(new { message = "请求过于频繁" }, statusCode: StatusCodes.Status429TooManyRequests);
            }
        }
        return await next(context);
    }
}

// 每个外部调用写一条审计（含密钥名/用户、路径、结果码）。
public sealed class ExternalAuditFilter(IDbContextFactory<ConfigDbContext> factory) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var result = await next(context);
        try
        {
            var status = http.Response.StatusCode;
            if (result is IStatusCodeHttpResult scr) status = scr.StatusCode ?? status;
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            var key = http.GetExternalKey();
            db.OperationLogs.Add(new OperationLog
            {
                UserId = http.User.GetUserId(),
                UserName = http.User.GetUserName(),
                Ts = ScadaTime.NowMs(),
                Action = $"{http.Request.Method} {http.Request.Path}",
                TargetType = "ExternalApi",
                TargetId = key?.Id.ToString() ?? http.User.GetUserId()?.ToString(),
                Details = JsonSerializer.Serialize(new { key = key?.Name, prefix = key?.KeyPrefix, query = http.Request.QueryString.Value }),
                Result = status is >= 200 and < 400 ? "成功" : status == 429 ? "拒绝：限流" : status == 401 ? "拒绝：未认证" : status == 403 ? "拒绝：权限不足" : $"HTTP {status}"
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch { }
        return result;
    }
}
