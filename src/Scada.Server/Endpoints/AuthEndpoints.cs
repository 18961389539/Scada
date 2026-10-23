using Microsoft.EntityFrameworkCore;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record LoginRequest(string UserName, string Password);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").AllowAnonymous();

        group.MapPost("/login", async (
            LoginRequest request,
            IDbContextFactory<ConfigDbContext> contextFactory,
            JwtTokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
            {
                return EndpointHelpers.BadRequest("用户名和密码不能为空");
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserName == request.UserName, cancellationToken);

            if (user is null || !user.Enabled || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            {
                return Results.Json(new { message = "用户名或密码错误" }, statusCode: StatusCodes.Status401Unauthorized);
            }

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
