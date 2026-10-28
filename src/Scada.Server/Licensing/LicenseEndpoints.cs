using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Endpoints;

namespace Scada.Server.Licensing;

public sealed record LicenseActivateRequest(string Code);

public static class LicenseEndpoints
{
    public static void MapLicenseEndpoints(this IEndpointRouteBuilder app)
    {
        // 过期后仍必须可读：前端靠它显示不可关闭的激活弹窗。
        app.MapGet("/api/license", (LicenseService license) => Results.Ok(license.Current))
            .RequireAuthorization(AccessPolicies.View);

        app.MapPost("/api/license/activate", async (
                LicenseActivateRequest request,
                LicenseService license,
                HttpContext http,
                IDbContextFactory<ConfigDbContext> contextFactory,
                CancellationToken cancellationToken) =>
            {
                var (ok, error) = license.Activate(request.Code ?? "");
                await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
                db.OperationLogs.Add(new OperationLog
                {
                    UserId = http.User.GetUserId(),
                    UserName = http.User.GetUserName(),
                    Ts = ScadaTime.NowMs(),
                    Action = "激活授权",
                    Result = ok ? "成功" : "失败：" + error
                });
                await db.SaveChangesAsync(cancellationToken);

                return ok
                    ? Results.Ok(license.Current)
                    : Results.BadRequest(new { message = error });
            })
            .RequireAuthorization(AccessPolicies.GlobalManageUsers)
            .AddEndpointFilter<AuditFailuresFilter>();
    }
}