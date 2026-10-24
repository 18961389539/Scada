using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record AlarmAckRequest(string? Note);
public sealed record AlarmNoteRequest(string? Note);

public static class AlarmEndpoints
{
    // 列表/详情对外的报警视图：在实体字段之外给出 ISA-18.2 状态。
    public static object View(AlarmEvent x) => new
    {
        x.Id, x.TagId, x.DeviceId, x.GatewayId, x.RuleId, x.Level, x.Value, x.StartTs, x.EndTs, x.EndReason,
        x.AckUser, x.AckTs, x.Message, x.Shelved, x.Suppressed, x.SuppressReason, x.Chattering,
        state = AlarmStates.Of(x)
    };

    public static void MapAlarmEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alarms");

        group.MapGet("", async (
            string? from,
            string? to,
            bool? activeOnly,
            bool? live,
            bool? unackedOnly,
            bool? includeShelved,
            int? limit,
            HttpContext http,
            AlarmService alarmService,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var defaultTo = EndpointHelpers.ParseTime(to, now);
            // 实时视图（活动或未确认）默认不限时间；“恢复未确认”也必须留在实时视图里，直到有人确认。
            var realtime = activeOnly == true || live == true;
            long? toMs = realtime && string.IsNullOrWhiteSpace(to) ? null : defaultTo;
            long? fromMs = realtime && string.IsNullOrWhiteSpace(from) ? null : EndpointHelpers.ParseTime(from, defaultTo - (long)TimeSpan.FromDays(1).TotalMilliseconds);
            var take = limit ?? 500;
            // 区域受限用户先多取再过滤，尽量保证返回条数接近请求上限。
            var fetch = access.HasGlobal(Permission.View) ? take : Math.Min(Math.Max(take, 1) * 4, 5000);
            var alarms = await alarmService.QueryAsync(fromMs, toMs, activeOnly ?? false, fetch, cancellationToken, unackedOnly == true, includeShelved ?? true, live == true);
            return Results.Ok(alarms.Where(x => EndpointHelpers.CanAlarm(access, Permission.View, x)).Take(Math.Clamp(take, 1, 5000)).Select(View).ToList());
        });

        group.MapGet("/{id:int}/timeline", async (int id, HttpContext http, AlarmService alarmService, CancellationToken ct) =>
        {
            var timeline = await alarmService.TimelineAsync(id, ct);
            if (timeline is not { } t) return Results.NotFound();
            var access = await http.GetAccessAsync();
            if (!EndpointHelpers.CanAlarm(access, Permission.View, t.Alarm)) return Results.NotFound();
            return Results.Ok(new { alarm = View(t.Alarm), activities = t.Activities, notifications = t.Notifications });
        });

        group.MapPost("/{id:int}/ack", async (
            int id,
            AlarmAckRequest? request,
            HttpContext http,
            AlarmService alarmService,
            IDbContextFactory<ConfigDbContext> factory,
            CancellationToken cancellationToken) =>
        {
            if (await RequireAlarmAsync(http, factory, id, $"确认报警 #{id}", cancellationToken) is { } denied) return denied;
            if (request?.Note is { Length: > 1000 }) return EndpointHelpers.BadRequest("备注不能超过 1000 字");
            var alarm = await alarmService.AckAsync(id, http.User.GetUserName(), request?.Note, cancellationToken);
            return alarm is not null ? Results.Ok(new { success = true, alarm = View(alarm) }) : Results.NotFound();
        }).RequireAuthorization(AccessPolicies.AckAlarm).AddEndpointFilter<AuditFailuresFilter>();

        group.MapPost("/{id:int}/notes", async (
            int id,
            AlarmNoteRequest request,
            HttpContext http,
            AlarmService alarmService,
            IDbContextFactory<ConfigDbContext> factory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Note)) return EndpointHelpers.BadRequest("备注不能为空");
            if (request.Note.Length > 1000) return EndpointHelpers.BadRequest("备注不能超过 1000 字");
            if (await RequireAlarmAsync(http, factory, id, $"报警备注 #{id}", cancellationToken) is { } denied) return denied;
            var activity = await alarmService.AddNoteAsync(id, http.User.GetUserName(), request.Note, cancellationToken);
            return activity is null ? Results.NotFound() : Results.Ok(activity);
        }).RequireAuthorization(AccessPolicies.AckAlarm).AddEndpointFilter<AuditFailuresFilter>();

        // 实时概况：各状态数量、暂挂/停用数、10 分钟洪水检测。
        group.MapGet("/summary", async (HttpContext http, AlarmService alarmService, AlarmKpiService kpi, AlarmShelfService shelf,
            IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            var live = (await alarmService.QueryAsync(null, null, false, 5000, ct, unackedOnly: false, includeShelved: true, liveOnly: true))
                .Where(x => EndpointHelpers.CanAlarm(access, Permission.View, x)).ToList();
            await shelf.EnsureLoadedAsync(ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var rules = await db.AlarmRules.AsNoTracking().Select(x => new { x.Id, x.TagId, x.OutOfService }).ToListAsync(ct);
            var visibleRules = rules.Where(x => access.CanTag(Permission.View, x.TagId)).ToList();
            var visibleRuleIds = visibleRules.Select(x => x.Id).ToHashSet();
            var flood = await kpi.FloodAsync(x => EndpointHelpers.CanAlarm(access, Permission.View, x), null, ct);
            var annunciated = live.Where(AlarmStates.Annunciated).ToList();
            return Results.Ok(new
            {
                unack = annunciated.Count(x => AlarmStates.Of(x) == AlarmStates.Unack),
                acked = annunciated.Count(x => AlarmStates.Of(x) == AlarmStates.Acked),
                rtnUnack = annunciated.Count(x => AlarmStates.Of(x) == AlarmStates.RtnUnack),
                shelved = live.Count(x => x.Shelved && x.EndTs == null),
                suppressed = live.Count(x => x.Suppressed && x.EndTs == null),
                chattering = live.Count(x => x.Chattering && x.EndTs == null),
                shelvedRules = shelf.Active.Count(x => visibleRuleIds.Contains(x.RuleId)),
                outOfServiceRules = visibleRules.Count(x => x.OutOfService),
                flood
            });
        }).RequireAuthorization(AccessPolicies.View);

        group.MapGet("/kpi", async (string? from, string? to, int? top, double? standingHours, HttpContext http, AlarmKpiService kpi, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var toMs = EndpointHelpers.ParseTime(to, now);
            var fromMs = EndpointHelpers.ParseTime(from, toMs - (long)TimeSpan.FromDays(1).TotalMilliseconds);
            if (toMs - fromMs > (long)TimeSpan.FromDays(92).TotalMilliseconds) return EndpointHelpers.BadRequest("统计区间不能超过 92 天");
            return Results.Ok(await kpi.ComputeAsync(fromMs, toMs, top ?? 10, standingHours, x => EndpointHelpers.CanAlarm(access, Permission.View, x), ct));
        }).RequireAuthorization(AccessPolicies.View);
    }

    private static async Task<IResult?> RequireAlarmAsync(HttpContext http, IDbContextFactory<ConfigDbContext> factory, int id, string action, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var alarm = await db.AlarmEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (alarm is null) return Results.NotFound();
        var access = await http.GetAccessAsync();
        return EndpointHelpers.CanAlarm(access, Permission.AckAlarm, alarm) ? null
            : await Audit.ForbidAsync(http, action, "无权处理该区域的报警", "Alarm", id);
    }
}
