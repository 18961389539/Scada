using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record LoginRequest(string UserName, string Password);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").AllowAnonymous();

        group.MapPost("/login", async (
            LoginRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            JwtTokenService tokenService,
            LoginThrottle throttle,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
            {
                return EndpointHelpers.BadRequest("用户名和密码不能为空");
            }

            var key = LoginThrottle.Key(request.UserName, http.Connection.RemoteIpAddress?.ToString());
            if (throttle.IsLocked(key, out var retryAfter))
            {
                var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return Results.Json(new { message = $"登录失败次数过多，请在 {seconds} 秒后重试" }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserName == request.UserName, cancellationToken);

            if (user is null || !user.Enabled || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            {
                throttle.RecordFailure(key);
                if (user is not null)
                {
                    // 已知账号的失败尝试写入审计，供管理员发现异常登录；未知用户名只记日志，避免被刷量污染审计表。
                    db.OperationLogs.Add(new OperationLog
                    {
                        UserId = user.Id,
                        UserName = user.UserName,
                        Ts = ScadaTime.NowMs(),
                        Action = "登录",
                        Result = user.Enabled ? "失败：密码错误" : "失败：账号已停用"
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }
                else
                {
                    loggerFactory.CreateLogger(nameof(AuthEndpoints)).LogWarning("登录失败：用户名 {UserName} 不存在，来源 {Address}", request.UserName, key);
                }

                return Results.Json(new { message = "用户名或密码错误" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.Reset(key);
            db.OperationLogs.Add(new OperationLog
            {
                UserId = user.Id,
                UserName = user.UserName,
                Ts = ScadaTime.NowMs(),
                Action = "登录",
                Result = "成功"
            });
            await db.SaveChangesAsync(cancellationToken);

            var (token, expiresAt) = tokenService.Create(user);
            return Results.Ok(new
            {
                token,
                expiresAt,
                user = new
                {
                    user.Id,
                    user.UserName,
                    user.DisplayName,
                    role = user.Role.ToString()
                }
            });
        });

        app.MapGet("/api/auth/me", async (
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetUserId();
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
            if (user is null || !user.Enabled)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                user.Id,
                user.UserName,
                user.DisplayName,
                role = user.Role.ToString()
            });
        });
    }
}
