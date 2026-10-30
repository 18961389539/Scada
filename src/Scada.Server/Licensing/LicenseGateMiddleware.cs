namespace Scada.Server.Licensing;

/// <summary>
/// 授权闸门：过期、无效或时钟异常时，除白名单内的登录、授权、本地化与健康检查接口外，
/// 所有 <c>/api</c> 与 SignalR 请求一律拒绝。前端静态资源放行，否则激活页都打不开。
/// </summary>
public sealed class LicenseGateMiddleware(RequestDelegate next)
{
    private static readonly string[] AllowedPrefixes =
    [
        "/api/auth",
        "/api/license",
        "/api/localization",
        "/health",
        "/api/health"
    ];

    public async Task InvokeAsync(HttpContext context, LicenseService license)
    {
        var snapshot = license.Current;
        if (snapshot.Blocked && IsGuarded(context.Request.Path) && !IsAllowed(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "license_blocked",
                status = snapshot.Status.ToString(),
                message = snapshot.Message
            });
            return;
        }

        await next(context);
    }

    private static bool IsGuarded(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");

    private static bool IsAllowed(PathString path) =>
        AllowedPrefixes.Any(prefix => path.StartsWithSegments(prefix));
}