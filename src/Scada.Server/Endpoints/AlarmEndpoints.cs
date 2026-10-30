using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record AlarmAckRequest(string? Note);
public sealed record AlarmNoteRequest(string? Note);

/// <summary>A filtered alarm page and its exact visible result count.</summary>
public sealed record AlarmPageResponse(IReadOnlyList<object> Items, int Total, int Offset, int Limit, bool Capped);

public static class AlarmEndpoints
{
    // 列表/详情对外的报警视图：在实体字段之外给出 ISA-18.2 状态。
    public static object View(AlarmEvent x, AccessContext? access = null) => new
    {
        x.Id, x.TagId, x.DeviceId, x.GatewayId, x.RuleId, x.Level, x.Value, x.StartTs, x.EndTs, x.EndReason,
        x.AckUser, x.AckTs, x.Message, x.Shelved, x.Suppressed, x.SuppressReason, x.Chattering, x.FirstOut,
        state = AlarmStates.Of(x),
        areas = Areas(access, x)
    };

    // 与 CanAlarm 同一套区域：有测点用测点区域，否则用设备区域。都没有时为空，表示系统报警按「任一确认权」处理。
    private static int[] Areas(AccessContext? access, AlarmEvent alarm)
    {
        if (access is null) return [];
        if (alarm.TagId is int tagId)
            return access.Model.TagAreas.TryGetValue(tagId, out var areas) ? areas : [];
        if (alarm.DeviceId is int deviceId && access.Model.DeviceAreas.TryGetValue(deviceId, out var area))
            return [area];
        return [];
    }

    private static AlarmQueryFilter VisibilityFilter(AccessContext access, params Permission[] permissions)
    {
        var restricted = !permissions.All(access.HasGlobal);
        return new AlarmQueryFilter(
            VisibleTagIds: restricted ? access.Model.TagAreas.Keys.Where(id => permissions.All(permission => access.CanTag(permission, id))).ToArray() : null,
            VisibleDeviceIds: restricted ? access.Model.DeviceAreas.Keys.Where(id => permissions.All(permission => access.CanDevice(permission, id))).ToArray() : null,
            IncludeSystemAlarms: permissions.All(access.HasAny));
    }

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
            bool? priorityFirst,
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
            var alarms = await alarmService.QueryAsync(fromMs, toMs, activeOnly ?? false, fetch, cancellationToken, unackedOnly == true, includeShelved ?? true, live == true, priorityFirst == true);
            return Results.Ok(alarms.Where(x => EndpointHelpers.CanAlarm(access, Permission.View, x)).Take(Math.Clamp(take, 1, 5000)).Select(x => View(x, access)).ToList());
        });

        group.MapGet("/page", async (
            string? from,
            string? to,
            bool? live,
            bool? priorityFirst,
            string? state,
            AlarmLevel? level,
            string? keyword,
            int? tagId,
            int? offset,
            int? limit,
            HttpContext http,
            AlarmService alarmService,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            AlarmListState? listState = null;
            if (state is not null)
            {
                if (!Enum.TryParse<AlarmListState>(state, true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    var stateOptions = string.Join("/", Enum.GetNames<AlarmListState>());
                    return EndpointHelpers.BadRequest($"报警状态筛选无效：{state}（可选 {stateOptions}）");
                }
                listState = parsed;
            }
            if (level is not null && !Enum.IsDefined(level.Value))
            {
                var levelOptions = string.Join("/", Enum.GetNames<AlarmLevel>());
                return EndpointHelpers.BadRequest($"报警等级筛选无效：{level}（可选 {levelOptions}）");
            }
            if (tagId is <= 0) return EndpointHelpers.BadRequest("测点 ID 必须大于 0");
            var defaultTo = EndpointHelpers.ParseTime(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            long? toMs = live == true && string.IsNullOrWhiteSpace(to) ? null : defaultTo;
            long? fromMs = live == true && string.IsNullOrWhiteSpace(from) ? null
                : EndpointHelpers.ParseTime(from, defaultTo - (long)TimeSpan.FromDays(1).TotalMilliseconds);
            if (fromMs > toMs) return EndpointHelpers.BadRequest("结束时间不能早于开始时间");
            var pageSize = Math.Clamp(limit ?? 100, 1, 500);
            var skip = Math.Max(offset ?? 0, 0);
            var filter = VisibilityFilter(access, Permission.View) with { State = listState, Level = level, Keyword = keyword, TagId = tagId };
            var total = await alarmService.CountAsync(fromMs, toMs, cancellationToken: cancellationToken, liveOnly: live == true, filter: filter);
            var items = await alarmService.QueryAsync(fromMs, toMs, false, pageSize, cancellationToken,
                liveOnly: live == true, priorityFirst: live == true && priorityFirst != false, skip: skip, filter: filter);
            return Results.Ok(new AlarmPageResponse(items.Select(x => View(x, access)).ToList(), total, skip, pageSize, false));
        })
            .WithSummary("Query a filtered live or historical alarm page")
            .WithDescription("Applies state, level, keyword, tag and area visibility before counting and paging. Live queries include active and recently returned unacknowledged alarms, ordered by severity then newest first unless priorityFirst=false. Historical queries use newest first.")
            .Produces<AlarmPageResponse>();

        group.MapGet("/{id:int}/timeline", async (int id, HttpContext http, AlarmService alarmService, CancellationToken ct) =>
        {
            var timeline = await alarmService.TimelineAsync(id, ct);
            if (timeline is not { } t) return Results.NotFound();
            var access = await http.GetAccessAsync();
            if (!EndpointHelpers.CanAlarm(access, Permission.View, t.Alarm)) return Results.NotFound();
            return Results.Ok(new { alarm = View(t.Alarm, access), activities = t.Activities, notifications = t.Notifications });
        });

        group.MapPost("/{id:int}/ack", async (
            int id,
            HttpContext http,
            AlarmService alarmService,
            IDbContextFactory<ConfigDbContext> factory,
            CancellationToken cancellationToken) =>
        {
            // 请求体可选：旧客户端无请求体（或任意 Content-Type）直接确认；带 JSON 时读取备注。
            var request = await ReadOptionalAsync<AlarmAckRequest>(http, cancellationToken);
            if (await RequireAlarmAsync(http, factory, id, $"确认报警 #{id}", cancellationToken) is { } denied) return denied;
            if (request?.Note is { Length: > 1000 }) return EndpointHelpers.BadRequest("备注不能超过 1000 字");
            var alarm = await alarmService.AckAsync(id, http.User.GetUserName(), request?.Note, cancellationToken);
            return alarm is not null ? Results.Ok(new { success = true, alarm = View(alarm, await http.GetAccessAsync()) }) : Results.NotFound();
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
            AlarmFloodGuard floodGuard, IOptions<ScadaSettings> options, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            var counts = await alarmService.CountStatesAsync(VisibilityFilter(access, Permission.View), ct);
            var ackableUnack = await alarmService.CountAsync(null, null, cancellationToken: ct, liveOnly: true,
                filter: VisibilityFilter(access, Permission.View, Permission.AckAlarm) with { State = AlarmListState.Unack });
            await shelf.EnsureLoadedAsync(ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var rules = await db.AlarmRules.AsNoTracking().Select(x => new { x.Id, x.TagId, x.OutOfService }).ToListAsync(ct);
            var visibleRules = rules.Where(x => access.CanTag(Permission.View, x.TagId)).ToList();
            var visibleRuleIds = visibleRules.Select(x => x.Id).ToHashSet();
            var flood = await kpi.FloodAsync(x => EndpointHelpers.CanAlarm(access, Permission.View, x), null, ct);
            // 运行期洪水按区域统计；区域受限用户只看到自己有权查看的区域。
            var allowedAreas = access.AreasWith(Permission.View);
            var floodAreas = floodGuard.Snapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Where(x => x.AreaId is int area ? allowedAreas is null || allowedAreas.Contains(area) : allowedAreas is null)
                .ToList();
            var areaNames = await db.Areas.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var policy = options.Value;
            return Results.Ok(new
            {
                unack = counts.Unack,
                // 一键确认只处理有 Ack 权的报警；确认框要用这个数，不能用上面按查看权统计的 unack。
                ackableUnack,
                acked = counts.Acked,
                rtnUnack = counts.RtnUnack,
                shelved = counts.Shelved,
                suppressed = counts.Suppressed,
                chattering = counts.Chattering,
                shelvedRules = shelf.Active.Count(x => visibleRuleIds.Contains(x.RuleId)),
                outOfServiceRules = visibleRules.Count(x => x.OutOfService),
                flood,
                floodGuard = new
                {
                    autoSuppress = policy.AlarmFloodAutoSuppress,
                    firstOut = policy.AlarmFloodFirstOut,
                    suppressMaxLevel = policy.AlarmFloodSuppressMaxLevel,
                    anyInFlood = floodAreas.Any(x => x.InFlood),
                    areas = floodAreas.Select(x => new
                    {
                        x.AreaId,
                        areaName = x.AreaId is int id ? areaNames.GetValueOrDefault(id) : null,
                        x.Count, x.Threshold, x.ExitThreshold, x.InFlood, x.WindowStartTs, x.WindowEndTs,
                        x.FloodStartTs, x.Peak, x.FirstOutEventId, x.SuppressedCount
                    })
                }
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

    private static async Task<T?> ReadOptionalAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (!http.Request.HasJsonContentType()) return null;
        try { return await http.Request.ReadFromJsonAsync<T>(ct); }
        catch (System.Text.Json.JsonException) { return null; }
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
