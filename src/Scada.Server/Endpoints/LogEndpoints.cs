using Scada.Server.Auth;
using Microsoft.EntityFrameworkCore;
using Scada.Data;

namespace Scada.Server.Endpoints;

public static class LogEndpoints
{
    public static void MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/logs", async (
            string? from,
            string? to,
            string? keyword,
            int? page,
            int? pageSize,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            var toMs = EndpointHelpers.ParseTime(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fromMs = EndpointHelpers.ParseTime(from, toMs - (long)TimeSpan.FromDays(7).TotalMilliseconds);
            var currentPage = Math.Max(page ?? 1, 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var query = db.OperationLogs.AsNoTracking()
                .Where(x => x.Ts >= fromMs && x.Ts <= toMs);

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var value = keyword.Trim();
                query = query.Where(x =>
                    x.UserName.Contains(value) ||
                    x.Action.Contains(value) ||
                    (x.Result != null && x.Result.Contains(value)));
            }

            var total = await query.CountAsync(cancellationToken);
            var items = await query
                .OrderByDescending(x => x.Ts)
                .Skip((currentPage - 1) * size)
                .Take(size)
                .ToListAsync(cancellationToken);

            return Results.Ok(new { total, page = currentPage, pageSize = size, items });
        }).RequireAuthorization(AccessPolicies.GlobalSystem);
    }
}
