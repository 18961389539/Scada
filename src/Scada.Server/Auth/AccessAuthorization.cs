using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;

namespace Scada.Server.Auth;

// 策略名：Perm:X 表示“至少在一个区域有 X”，Global:X 表示“全部区域都有 X”。区域级细判在端点内完成。
public static class AccessPolicies
{
    public static string Any(Permission permission) => "Perm:" + permission;
    public static string Global(Permission permission) => "Global:" + permission;

    public const string View = "Perm:View";
    public const string Control = "Perm:Control";
    public const string AckAlarm = "Perm:AckAlarm";
    public const string Configure = "Perm:Configure";
    public const string GlobalConfigure = "Global:Configure";
    public const string GlobalManageUsers = "Global:ManageUsers";
    public const string GlobalSystem = "Global:System";

    public static void Register(AuthorizationOptions options)
    {
        foreach (var permission in Permissions.Each)
        {
            options.AddPolicy(Any(permission), p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission, false)));
            options.AddPolicy(Global(permission), p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission, true)));
        }
    }
}

public sealed record PermissionRequirement(Permission Permission, bool Global) : IAuthorizationRequirement;

public sealed class PermissionHandler(AccessModelProvider provider) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var access = context.Resource is HttpContext http ? await http.GetAccessAsync() : await provider.ForUserAsync(context.User.GetUserId());
        if (requirement.Global ? access.HasGlobal(requirement.Permission) : access.HasAny(requirement.Permission))
            context.Succeed(requirement);
    }
}

// 被策略拒绝的写操作写入审计（读操作拒绝只返回 403，避免刷屏）。
public sealed class AuditingAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            try
            {
                var factory = context.RequestServices.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
                await Audit.DeniedAsync(factory, context, $"{context.Request.Method} {context.Request.Path}", "权限不足", CancellationToken.None);
            }
            catch { }
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

public static class AccessHttpExtensions
{
    private const string ItemKey = "scada.access";

    public static async Task<AccessContext> GetAccessAsync(this HttpContext http)
    {
        if (http.Items.TryGetValue(ItemKey, out var cached) && cached is AccessContext context) return context;
        var provider = http.RequestServices.GetRequiredService<AccessModelProvider>();
        context = await provider.ForUserAsync(http.User.GetUserId(), http.RequestAborted);
        http.Items[ItemKey] = context;
        return context;
    }
}

// 审计：记录配置变更的字段差分，以及失败/被拒绝的操作。
public static class Audit
{
    public static string Diff(object? before, object? after) => AuditDiff.Compute(before, after);

    public static OperationLog Change(HttpContext http, string action, string targetType, object? targetId, object? before, object? after, string result = "成功") => new()
    {
        UserId = http.User.GetUserId(),
        UserName = http.User.GetUserName(),
        Ts = ScadaTime.NowMs(),
        Action = action,
        TargetType = targetType,
        TargetId = targetId?.ToString(),
        Details = Diff(before, after),
        Result = result
    };

    public static async Task DeniedAsync(IDbContextFactory<ConfigDbContext> factory, HttpContext http, string action, string reason, CancellationToken ct, string? targetType = null, object? targetId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.OperationLogs.Add(new OperationLog
        {
            UserId = http.User.GetUserId(),
            UserName = http.User.GetUserName(),
            Ts = ScadaTime.NowMs(),
            Action = action.Length > 200 ? action[..200] : action,
            TargetType = targetType,
            TargetId = targetId?.ToString(),
            Result = "拒绝：" + reason
        });
        await db.SaveChangesAsync(ct);
    }

    public static async Task FailedAsync(IDbContextFactory<ConfigDbContext> factory, HttpContext http, string action, string reason, CancellationToken ct, string? targetType = null, object? targetId = null, object? attempted = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.OperationLogs.Add(new OperationLog
        {
            UserId = http.User.GetUserId(),
            UserName = http.User.GetUserName(),
            Ts = ScadaTime.NowMs(),
            Action = action.Length > 200 ? action[..200] : action,
            TargetType = targetType,
            TargetId = targetId?.ToString(),
            Details = attempted is null ? null : Diff(null, attempted),
            Result = Truncate("失败：" + reason, 500)
        });
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    // 区域级拒绝：记审计并返回 403。
    public static async Task<IResult> ForbidAsync(HttpContext http, string action, string reason, string? targetType = null, object? targetId = null)
    {
        if (!HttpMethods.IsGet(http.Request.Method))
        {
            try
            {
                var factory = http.RequestServices.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
                await Audit.DeniedAsync(factory, http, action, reason, CancellationToken.None, targetType, targetId);
            }
            catch { }
        }

        return Results.Json(new { message = reason }, statusCode: StatusCodes.Status403Forbidden);
    }
}
