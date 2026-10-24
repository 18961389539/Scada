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
            AlarmService alarmService,
            CancellationToken cancellationToken) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var defaultTo = EndpointHelpers.ParseTime(to, now);
            long? toMs = activeOnly == true && string.IsNullOrWhiteSpace(to) ? null : defaultTo;
            long? fromMs = activeOnly == true && string.IsNullOrWhiteSpace(from) ? null : EndpointHelpers.ParseTime(from, defaultTo - (long)TimeSpan.FromDays(1).TotalMilliseconds);
            var alarms = await alarmService.QueryAsync(
                fromMs,
                toMs,
                activeOnly ?? false,
                limit ?? 500,
                cancellationToken);
            return Results.Ok(alarms);
        });

        group.MapPost("/{id:int}/ack", async (
            int id,
            HttpContext http,
            AlarmService alarmService,
            CancellationToken cancellationToken) =>
        {
            var ok = await alarmService.AckAsync(id, http.User.GetUserName(), cancellationToken);
            return ok ? Results.Ok(new { success = true }) : Results.NotFound();
        }).RequireAuthorization("Operator");
    }
}
