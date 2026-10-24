using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record UserCreateRequest(string UserName, string Password, string DisplayName, UserRole Role, bool Enabled);
public sealed record UserUpdateRequest(string DisplayName, UserRole Role, bool Enabled);
public sealed record PasswordResetRequest(string Password);

public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").RequireAuthorization(AccessPolicies.GlobalManageUsers).AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("", async (
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var users = await db.Users.AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.UserName,
                    x.DisplayName,
                    role = x.Role.ToString(),
                    x.Enabled
                })
                .ToListAsync(cancellationToken);
            return Results.Ok(users);
        });

        group.MapPost("", async (
            UserCreateRequest request,
            HttpContext http,
            AccessModelProvider access,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length < 3)
            {
                return EndpointHelpers.BadRequest("用户名至少 3 个字符");
            }

            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
            {
                return EndpointHelpers.BadRequest("密码至少 6 个字符");
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var userName = request.UserName.Trim();
            if (await db.Users.AnyAsync(x => x.UserName == userName, cancellationToken))
            {
                return EndpointHelpers.BadRequest("用户名已存在");
            }

            var user = new User
            {
                UserName = userName,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? userName : request.DisplayName.Trim(),
                PasswordHash = PasswordHasher.Hash(request.Password),
                Role = request.Role,
                Enabled = request.Enabled
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            db.OperationLogs.Add(Audit.Change(http, $"创建用户：{user.UserName}", "User", user.Id, null, Snapshot(user)));
            await db.SaveChangesAsync(cancellationToken);
            access.Invalidate();
            return Results.Created($"/api/users/{user.Id}", new
            {
                user.Id,
                user.UserName,
                user.DisplayName,
                role = user.Role.ToString(),
                user.Enabled
            });
        });

        group.MapPut("/{id:int}", async (
            int id,
            UserUpdateRequest request,
            HttpContext http,
            AccessModelProvider access,
            IDbContextFactory<ConfigDbContext> contextFactory,
            SessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            var previousRole = user.Role;
            var previousEnabled = user.Enabled;
            var before = Snapshot(user);

            if (user.Role == UserRole.Admin && request.Role != UserRole.Admin)
            {
                var adminCount = await db.Users.CountAsync(x => x.Role == UserRole.Admin && x.Enabled, cancellationToken);
                if (adminCount <= 1)
                {
                    return EndpointHelpers.BadRequest("不能取消最后一个管理员的角色");
                }
            }

            user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? user.UserName : request.DisplayName.Trim();
            user.Role = request.Role;
            user.Enabled = request.Enabled;

            if (user.Role == UserRole.Admin && !user.Enabled)
            {
                var adminCount = await db.Users.CountAsync(x => x.Role == UserRole.Admin && x.Enabled, cancellationToken);
                if (adminCount <= 1)
                {
                    return EndpointHelpers.BadRequest("不能停用最后一个管理员");
                }
            }

            db.OperationLogs.Add(Audit.Change(http, $"修改用户：{user.UserName}", "User", user.Id, before, Snapshot(user)));
            await db.SaveChangesAsync(cancellationToken);

            access.Invalidate();
            // 角色变化或停用后，已建立的实时连接必须立即断开，否则旧权限会继续生效到客户端重连为止。
            if (previousRole != user.Role || (previousEnabled && !user.Enabled))
            {
                sessions.RevokeUser(user.Id);
            }

            return Results.Ok(new
            {
                user.Id,
                user.UserName,
                user.DisplayName,
                role = user.Role.ToString(),
                user.Enabled
            });
        });

        group.MapPost("/{id:int}/password", async (
            int id,
            PasswordResetRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            SessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
            {
                return EndpointHelpers.BadRequest("密码至少 6 个字符");
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            user.PasswordHash = PasswordHasher.Hash(request.Password);
            db.OperationLogs.Add(Audit.Change(http, $"重置密码：{user.UserName}", "User", user.Id, new { password = "old" }, new { password = "new" }));
            await db.SaveChangesAsync(cancellationToken);
            sessions.RevokeUser(user.Id);
            return Results.Ok(new { success = true });
        });

        group.MapDelete("/{id:int}", async (
            int id,
            HttpContext http,
            AccessModelProvider access,
            IDbContextFactory<ConfigDbContext> contextFactory,
            SessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            if (http.User.GetUserId() == id)
            {
                return EndpointHelpers.BadRequest("不能删除当前登录用户");
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (user.Role == UserRole.Admin)
            {
                var adminCount = await db.Users.CountAsync(x => x.Role == UserRole.Admin, cancellationToken);
                if (adminCount <= 1)
                {
                    return EndpointHelpers.BadRequest("不能删除最后一个管理员");
                }
            }

            db.Users.Remove(user);
            db.OperationLogs.Add(Audit.Change(http, $"删除用户：{user.UserName}", "User", user.Id, Snapshot(user), null));
            await db.SaveChangesAsync(cancellationToken);
            access.Invalidate();
            sessions.RevokeUser(id);
            return Results.NoContent();
        });
    }

    private static object Snapshot(User user) => new { user.UserName, user.DisplayName, Role = user.Role.ToString(), user.Enabled };
}
