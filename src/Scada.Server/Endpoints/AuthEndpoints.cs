using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record LoginRequest(string UserName, string Password);
public sealed record LocaleRequest(string? Locale);
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
/// <summary>Public site identity and explicitly enabled demo presentation, without operational configuration.</summary>
public sealed record SiteInfoResponse(string SiteName, bool DemoMode);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").AllowAnonymous();

        group.MapGet("/site-info", (IOptions<ScadaSettings> options, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new SiteInfoResponse(options.Value.SiteName.Trim(), options.Value.DemoMode));
        }).WithName("GetSiteInfo").WithSummary("Get public site identity and demo display mode").Produces<SiteInfoResponse>();

        group.MapPost("/login", async (
            LoginRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            JwtTokenService tokenService,
            LoginThrottle throttle,
            TokenUserCache tokenCache,
            Scada.Data.AccessModelProvider access,
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

            // 登录成功意味着账号刚通过密码校验：清掉校验缓存，避免"刚被管理员启用"的账号
            // 因为 5 秒窗口内还留着旧的停用状态而拿到一个立刻被判失效的令牌。
            tokenCache.Invalidate(user.Id);
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
                    role = user.Role.ToString(),
                    user.Locale,
                    user.MustChangePassword,
                    access = AccessEndpoints.Describe(await access.ForUserAsync(user.Id, cancellationToken))
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
                role = user.Role.ToString(),
                user.Locale,
                user.MustChangePassword,
                access = AccessEndpoints.Describe(await http.GetAccessAsync())
            });
        });

        // 自助改密：首装管理员与管理员重置过口令的账号必须由本人换掉，换完立刻签发新令牌。
        // 口令变了 CredentialsVersion 就变，旧令牌会失效，所以这里必须连新令牌一起返回，
        // 否则用户改完密码就被踢回登录页，体验上像是改失败了。
        app.MapPost("/api/auth/password", async (
            PasswordChangeRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            JwtTokenService tokenService,
            Scada.Data.AccessModelProvider access,
            LoginThrottle throttle,
            TokenUserCache tokenCache,
            CancellationToken cancellationToken) =>
        {
            var userId = http.User.GetUserId();
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.CurrentPassword)) return EndpointHelpers.BadRequest("请输入当前密码");
            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
                return EndpointHelpers.BadRequest("新密码至少需要 6 个字符");

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
            if (user is null || !user.Enabled) return Results.Unauthorized();
            if (!PasswordHasher.Verify(request.CurrentPassword, user.PasswordHash))
                return EndpointHelpers.BadRequest("当前密码不正确");
            if (PasswordHasher.Verify(request.NewPassword, user.PasswordHash))
                return EndpointHelpers.BadRequest("新密码不能与当前密码相同");

            user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
            user.MustChangePassword = false;
            user.PasswordChangedTs = ScadaTime.NowMs();
            db.OperationLogs.Add(Audit.Change(http, $"修改密码：{user.UserName}", "User", user.Id,
                new { password = "old" }, new { password = "new" }));
            await db.SaveChangesAsync(cancellationToken);
            // 密码变了，凭据版本随之改变：清掉令牌校验缓存，否则旧缓存会让刚签发的新令牌在 5 秒窗口内被判为失效。
            tokenCache.Invalidate(user.Id);
            // 改密成功说明就是本人在操作，顺手解掉登录失败锁定，避免他刚解锁又被自己卡住。
            throttle.Reset(LoginThrottle.Key(user.UserName, http.Connection.RemoteIpAddress?.ToString()));

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
                    role = user.Role.ToString(),
                    user.Locale,
                    user.MustChangePassword,
                    access = AccessEndpoints.Describe(await access.ForUserAsync(user.Id, cancellationToken))
                }
            });
        }).RequireAuthorization();

        // 个人语言偏好（按用户保存在服务端，任何已登录用户可改自己的）。
        app.MapPut("/api/auth/me/locale", async (LocaleRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> contextFactory, CancellationToken cancellationToken) =>
        {
            if (request.Locale is not (Scada.Server.Localization.Localizer.ZhCn or Scada.Server.Localization.Localizer.EnUs))
                return EndpointHelpers.BadRequest("语言仅支持 zh-CN 或 en-US");
            var userId = http.User.GetUserId();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
            if (user is null || !user.Enabled) return Results.Unauthorized();
            user.Locale = request.Locale;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
