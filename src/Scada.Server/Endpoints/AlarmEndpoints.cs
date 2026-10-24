using Microsoft.EntityFrameworkCore;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public static class AlarmEndpoints
{
    public static void MapAlarmEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alarms");

        group.MapGet("", async (
            string? from,
            string? to,
            bool? activeOnly,
            int? limit,
            HttpContext http,
            AlarmService alarmService,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var defaultTo = EndpointHelpers.ParseTime(to, now);
            long? toMs = activeOnly == true && string.IsNullOrWhiteSpace(to) ? null : defaultTo;
            long? fromMs = activeOnly == true && string.IsNullOrWhiteSpace(from) ? null : EndpointHelpers.ParseTime(from, defaultTo - (long)TimeSpan.FromDays(1).TotalMilliseconds);
            var take = limit ?? 500;
            // 区域受限用户先多取再过滤，尽量保证返回条数接近请求上限。
            var fetch = access.HasGlobal(Permission.View) ? take : Math.Min(Math.Max(take, 1) * 4, 5000);
            var alarms = await alarmService.QueryAsync(
                fromMs,
                toMs,
                activeOnly ?? false,
                fetch,
                cancellationToken);
            return Results.Ok(alarms.Where(x => EndpointHelpers.CanAlarm(access, Permission.View, x)).Take(Math.Clamp(take, 1, 5000)).ToList());
        });

        group.MapPost("/{id:int}/ack", async (
            int id,
            HttpContext http,
            AlarmService alarmService,
            IDbContextFactory<ConfigDbContext> factory,
            CancellationToken cancellationToken) =>
        {
            await using (var db = await factory.CreateDbContextAsync(cancellationToken))
            {
                var alarm = await db.AlarmEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (alarm is null) return Results.NotFound();
                var access = await http.GetAccessAsync();
                if (!EndpointHelpers.CanAlarm(access, Permission.AckAlarm, alarm))
                    return await Audit.ForbidAsync(http, $"确认报警 #{id}", "无权确认该区域的报警", "Alarm", id);
            }

            var ok = await alarmService.AckAsync(id, http.User.GetUserName(), cancellationToken);
            return ok ? Results.Ok(new { success = true }) : Results.NotFound();
        }).RequireAuthorization(AccessPolicies.AckAlarm);
    }
}
