using System.Security.Claims;

namespace Scada.Server.Auth;

/// <summary>
/// 首装口令与管理员重置后的口令必须由本人换掉：未改密前除「查自己」与「改密」外一律 428。
/// 放在认证之后、授权之前——先确认是谁，再决定能不能放行；这样即使角色有权，也先走改密。
/// 令牌里已带 <c>mustChangePassword</c> 快照，不额外查库。
/// </summary>
public sealed class MustChangePasswordMiddleware(RequestDelegate next)
{
    private static readonly PathString[] Allowed =
    [
        "/api/auth/password",
        "/api/auth/me",
        "/api/auth/me/locale",
        // 续期不改变账号状态；未改密时新令牌仍带 mustChangePassword，继续被本中间件拦。
        "/api/auth/refresh"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirstValue("mustChangePassword") == "true"
            && !Allowed.Any(path => context.Request.Path.StartsWithSegments(path)))
        {
            context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
            await context.Response.WriteAsJsonAsync(new { message = "首次登录或密码已被重置，请先修改密码" });
            return;
        }

        await next(context);
    }
}
