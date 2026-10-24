using Scada.Core.Models;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Notifications;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record NotificationChannelRequest(string Name, NotificationChannelType Type, bool Enabled, NotificationChannelSettings? Settings, string? Secret);
public sealed record DutyShiftRequest(string Name, int StartMinute, int EndMinute, int Days, bool Enabled);
public sealed record NotificationRouteRequest(string Name, bool Enabled, int ChannelId, int? AreaId, AlarmLevel MinLevel, int? ShiftId, string? Recipients, bool OnRaise, bool OnRecover, int? EscalateAfterMinutes);
public sealed record ChannelTestRequest(string? Recipients);
public sealed record OutboxAcknowledgeRequest(long[]? Ids);

// 报警通知配置：通道（含加密凭据）与班次是跨区域对象，需全局配置权限；路由按区域授权，区域管理员只能管理本区域路由。
public static class NotificationEndpoints
{
    public const string Mask = "***";

    // 对外/审计视图：凭据永不回显，只给出是否已设置。
    public static object View(NotificationChannel c) => new
    {
        c.Id, c.Name, c.Type, c.Enabled,
        settings = NotificationChannelSettings.Parse(c.SettingsJson),
        hasSecret = !string.IsNullOrEmpty(c.SecretProtected),
        secret = string.IsNullOrEmpty(c.SecretProtected) ? null : Mask
    };

    private static object AuditView(NotificationChannel c) => new
    {
        c.Name, c.Type, c.Enabled, settings = c.SettingsJson,
        // AuditDiff 对 secret 字段只记录“已变化”；这里用密文摘要让差分能看出凭据被改过，但不泄露内容。
        secret = string.IsNullOrEmpty(c.SecretProtected) ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.SecretProtected)))[..12]
    };

    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        // 发件箱最终失败：按报警所在区域的配置权限查看与“已知悉/清除”；知悉后不再计入 /health 告警。
        var outbox = app.MapGroup("/api/notification-outbox").AddEndpointFilter<AuditFailuresFilter>().RequireAuthorization(AccessPolicies.Configure);
        outbox.MapGet("/failed", async (bool? includeAcknowledged, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            return Results.Ok((await FailedAsync(factory, includeAcknowledged == true, ct)).Where(x => x.Can(access))
                .Select(x => new { x.Item.Id, x.Item.AlarmEventId, alarmMessage = x.Message, x.Item.ReportRunId, x.Item.RouteId, x.Item.ChannelId, x.Item.Action, x.Item.Attempts, x.Item.LastError, x.Item.FinishedTs, x.Item.AcknowledgedTs, x.Item.AcknowledgedBy }));
        });
        outbox.MapPost("/acknowledge", async (OutboxAcknowledgeRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, AlarmNotificationDispatcher dispatcher, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            var rows = await FailedAsync(factory, false, ct);
            if (request.Ids is { Length: > 0 } ids) rows = rows.Where(x => ids.Contains(x.Item.Id)).ToList();
            var denied = rows.Where(x => !x.Can(access)).Select(x => x.Item.Id).ToArray();
            if (request.Ids is { Length: > 0 } && denied.Length > 0)
                return await Audit.ForbidAsync(http, "知悉通知最终失败", $"无权处理通知：{string.Join(",", denied.Take(20))}", "NotificationOutbox", string.Join(",", denied.Take(20)));
            var allowed = rows.Where(x => !denied.Contains(x.Item.Id)).Select(x => x.Item.Id).ToArray();
            var now = ScadaTime.NowMs(); var user = http.User.GetUserName();
            await using var db = await factory.CreateDbContextAsync(ct);
            var updated = allowed.Length == 0 ? 0 : await db.NotificationOutbox.Where(x => allowed.Contains(x.Id) && x.AcknowledgedTs == null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.AcknowledgedTs, now).SetProperty(x => x.AcknowledgedBy, user), ct);
            db.OperationLogs.Add(Audit.Change(http, "知悉通知最终失败", "NotificationOutbox", string.Join(",", allowed.Take(50)), new { acknowledged = 0 }, new { acknowledged = updated, ids = allowed.Take(50) }));
            await db.SaveChangesAsync(ct);
            await dispatcher.RefreshStatsAsync(ct);
            return Results.Ok(new { acknowledged = updated });
        });

        var channels = app.MapGroup("/api/notification-channels").AddEndpointFilter<AuditFailuresFilter>();
        // 路由编辑需要选通道，区域配置员可读通道列表（凭据已脱敏）。
        channels.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok((await db.NotificationChannels.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)).Select(View));
        }).RequireAuthorization(AccessPolicies.Configure);

        channels.MapPost("", async (NotificationChannelRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, ISecretProtector secrets, CancellationToken ct) =>
        {
            var channel = new NotificationChannel();
            if (Apply(channel, request, secrets) is { } error) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.NotificationChannels.AnyAsync(x => x.Name == channel.Name, ct)) return EndpointHelpers.BadRequest("通道名称已存在");
            db.NotificationChannels.Add(channel);
            await db.SaveChangesAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"创建通知通道：{channel.Name}", "NotificationChannel", channel.Id, null, AuditView(channel)));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/notification-channels/{channel.Id}", View(channel));
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        channels.MapPut("/{id:int}", async (int id, NotificationChannelRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, ISecretProtector secrets, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var channel = await db.NotificationChannels.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (channel is null) return Results.NotFound();
            var before = AuditView(channel);
            if (Apply(channel, request, secrets) is { } error) return EndpointHelpers.BadRequest(error);
            if (await db.NotificationChannels.AnyAsync(x => x.Id != id && x.Name == channel.Name, ct)) return EndpointHelpers.BadRequest("通道名称已存在");
            db.OperationLogs.Add(Audit.Change(http, $"修改通知通道：{channel.Name}", "NotificationChannel", channel.Id, before, AuditView(channel)));
            await db.SaveChangesAsync(ct);
            return Results.Ok(View(channel));
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        channels.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var channel = await db.NotificationChannels.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (channel is null) return Results.NotFound();
            if (await db.NotificationRoutes.AnyAsync(x => x.ChannelId == id, ct)) return EndpointHelpers.BadRequest("仍有通知路由使用该通道，请先删除路由");
            db.NotificationChannels.Remove(channel);
            db.OperationLogs.Add(Audit.Change(http, $"删除通知通道：{channel.Name}", "NotificationChannel", channel.Id, AuditView(channel), null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        channels.MapPost("/{id:int}/test", async (int id, ChannelTestRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, AlarmNotificationDispatcher dispatcher, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var channel = await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (channel is null) return Results.NotFound();
            string result;
            try
            {
                await dispatcher.SendTestAsync(channel, AlarmNotificationDispatcher.SplitRecipients(request.Recipients), ct);
                result = "成功";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                result = $"失败：{ex.Message}";
            }
            db.OperationLogs.Add(Audit.Change(http, $"测试通知通道：{channel.Name}", "NotificationChannel", channel.Id, null, null, result));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { success = result == "成功", message = result });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        var shifts = app.MapGroup("/api/duty-shifts").AddEndpointFilter<AuditFailuresFilter>();
        shifts.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(await db.DutyShifts.AsNoTracking().OrderBy(x => x.StartMinute).ToListAsync(ct));
        }).RequireAuthorization(AccessPolicies.Configure);

        shifts.MapPost("", async (DutyShiftRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var shift = new DutyShift();
            if (Apply(shift, request) is { } error) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            if (await db.DutyShifts.AnyAsync(x => x.Name == shift.Name, ct)) return EndpointHelpers.BadRequest("班次名称已存在");
            db.DutyShifts.Add(shift);
            await db.SaveChangesAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"创建班次：{shift.Name}", "DutyShift", shift.Id, null, shift));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/duty-shifts/{shift.Id}", shift);
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        shifts.MapPut("/{id:int}", async (int id, DutyShiftRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var shift = await db.DutyShifts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (shift is null) return Results.NotFound();
            var before = new DutyShift { Id = shift.Id, Name = shift.Name, StartMinute = shift.StartMinute, EndMinute = shift.EndMinute, Days = shift.Days, Enabled = shift.Enabled };
            if (Apply(shift, request) is { } error) return EndpointHelpers.BadRequest(error);
            if (await db.DutyShifts.AnyAsync(x => x.Id != id && x.Name == shift.Name, ct)) return EndpointHelpers.BadRequest("班次名称已存在");
            db.OperationLogs.Add(Audit.Change(http, $"修改班次：{shift.Name}", "DutyShift", shift.Id, before, shift));
            await db.SaveChangesAsync(ct);
            return Results.Ok(shift);
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        shifts.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var shift = await db.DutyShifts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (shift is null) return Results.NotFound();
            if (await db.NotificationRoutes.AnyAsync(x => x.ShiftId == id, ct)) return EndpointHelpers.BadRequest("仍有通知路由引用该班次");
            db.DutyShifts.Remove(shift);
            db.OperationLogs.Add(Audit.Change(http, $"删除班次：{shift.Name}", "DutyShift", shift.Id, shift, null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        var routes = app.MapGroup("/api/notification-routes").AddEndpointFilter<AuditFailuresFilter>();
        routes.MapGet("", async (HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var list = await db.NotificationRoutes.AsNoTracking().OrderBy(x => x.AreaId).ThenBy(x => x.Name).ToListAsync(ct);
            return Results.Ok(list.Where(x => CanRoute(access, x.AreaId)));
        }).RequireAuthorization(AccessPolicies.Configure);

        routes.MapPost("", async (NotificationRouteRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (await RequireRouteAreaAsync(http, request.AreaId, $"创建通知路由：{request.Name}") is { } denied) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var route = new NotificationRoute();
            if (await ApplyAsync(db, route, request, ct) is { } error) return EndpointHelpers.BadRequest(error);
            db.NotificationRoutes.Add(route);
            await db.SaveChangesAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"创建通知路由：{route.Name}", "NotificationRoute", route.Id, null, route));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/notification-routes/{route.Id}", route);
        }).RequireAuthorization(AccessPolicies.Configure);

        routes.MapPut("/{id:int}", async (int id, NotificationRouteRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var route = await db.NotificationRoutes.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (route is null) return Results.NotFound();
            // 改区域需新旧两个区域都有配置权限。
            if (await RequireRouteAreaAsync(http, route.AreaId, $"修改通知路由 #{id}") is { } denied) return denied;
            if (await RequireRouteAreaAsync(http, request.AreaId, $"修改通知路由 #{id}") is { } denied2) return denied2;
            var before = CopyRoute(route);
            if (await ApplyAsync(db, route, request, ct) is { } error) return EndpointHelpers.BadRequest(error);
            db.OperationLogs.Add(Audit.Change(http, $"修改通知路由：{route.Name}", "NotificationRoute", route.Id, before, route));
            await db.SaveChangesAsync(ct);
            return Results.Ok(route);
        }).RequireAuthorization(AccessPolicies.Configure);

        routes.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var route = await db.NotificationRoutes.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (route is null) return Results.NotFound();
            if (await RequireRouteAreaAsync(http, route.AreaId, $"删除通知路由 #{id}") is { } denied) return denied;
            db.NotificationRoutes.Remove(route);
            db.OperationLogs.Add(Audit.Change(http, $"删除通知路由：{route.Name}", "NotificationRoute", route.Id, route, null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);
    }

    private static bool CanRoute(AccessContext access, int? areaId) =>
        areaId is int area ? access.Has(Permission.Configure, area) : access.HasGlobal(Permission.Configure);

    private static async Task<IResult?> RequireRouteAreaAsync(HttpContext http, int? areaId, string action)
    {
        var access = await http.GetAccessAsync();
        if (CanRoute(access, areaId)) return null;
        return await Audit.ForbidAsync(http, action, areaId is null ? "全区域路由需要全局配置权限" : $"无权配置区域 #{areaId} 的通知", "NotificationRoute", areaId);
    }

    private static string? Apply(NotificationChannel channel, NotificationChannelRequest request, ISecretProtector secrets)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return "通道名称不能为空且不超过 100 字";
        if (!Enum.IsDefined(request.Type)) return "通道类型无效";
        var settings = request.Settings ?? new NotificationChannelSettings();
        settings.Host = settings.Host?.Trim(); settings.Url = settings.Url?.Trim(); settings.From = settings.From?.Trim();
        settings.UserName = string.IsNullOrWhiteSpace(settings.UserName) ? null : settings.UserName.Trim();
        settings.HeaderName = string.IsNullOrWhiteSpace(settings.HeaderName) ? null : settings.HeaderName.Trim();
        if (settings.Validate(request.Type) is { } error) return error;
        channel.Name = name;
        channel.Type = request.Type;
        channel.Enabled = request.Enabled;
        channel.SettingsJson = settings.ToJson();
        // 凭据：null 或 *** 表示保持不变，空串表示清除，其他值加密保存。
        if (request.Secret is not null && request.Secret != Mask)
        {
            if (request.Secret.Length > 2000) return "凭据过长";
            channel.SecretProtected = request.Secret.Length == 0 ? null : secrets.Protect(request.Secret);
        }
        return null;
    }

    private static string? Apply(DutyShift shift, DutyShiftRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return "班次名称不能为空且不超过 100 字";
        if (request.StartMinute is < 0 or > 1439 || request.EndMinute is < 0 or > 1439) return "班次起止时间无效";
        if (request.Days is < 1 or > 127) return "至少选择一天";
        shift.Name = name; shift.StartMinute = request.StartMinute; shift.EndMinute = request.EndMinute; shift.Days = request.Days; shift.Enabled = request.Enabled;
        return null;
    }

    private static async Task<string?> ApplyAsync(ConfigDbContext db, NotificationRoute route, NotificationRouteRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return "路由名称不能为空且不超过 100 字";
        var channel = await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.ChannelId, ct);
        if (channel is null) return "通知通道不存在";
        if (request.AreaId is int area && !await db.Areas.AnyAsync(x => x.Id == area, ct)) return "区域不存在";
        if (request.ShiftId is int shift && !await db.DutyShifts.AnyAsync(x => x.Id == shift, ct)) return "班次不存在";
        if (!Enum.IsDefined(request.MinLevel)) return "等级无效";
        if (request.EscalateAfterMinutes is < 1 or > 10080) return "升级时间须在 1～10080 分钟之间";
        var recipients = AlarmNotificationDispatcher.SplitRecipients(request.Recipients);
        if (channel.Type != NotificationChannelType.Webhook && recipients.Count == 0) return "邮件/短信路由必须填写收件人";
        if (channel.Type == NotificationChannelType.Email && recipients.Any(x => !System.Net.Mail.MailAddress.TryCreate(x, out _))) return "收件邮箱格式无效";
        if (channel.Type == NotificationChannelType.SmsHttp && recipients.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, @"^\+?[0-9]{5,20}$"))) return "手机号格式无效";
        if (request.EscalateAfterMinutes is null && !request.OnRaise && !request.OnRecover) return "请至少选择触发或恢复通知，或设置升级时间";
        route.Name = name; route.Enabled = request.Enabled; route.ChannelId = request.ChannelId; route.AreaId = request.AreaId;
        route.MinLevel = request.MinLevel; route.ShiftId = request.ShiftId; route.Recipients = string.Join(",", recipients);
        route.OnRaise = request.EscalateAfterMinutes is null && request.OnRaise;
        route.OnRecover = request.EscalateAfterMinutes is null && request.OnRecover;
        route.EscalateAfterMinutes = request.EscalateAfterMinutes;
        return null;
    }

    private static NotificationRoute CopyRoute(NotificationRoute r) => new()
    {
        Id = r.Id, Name = r.Name, Enabled = r.Enabled, ChannelId = r.ChannelId, AreaId = r.AreaId, MinLevel = r.MinLevel, ShiftId = r.ShiftId,
        Recipients = r.Recipients, OnRaise = r.OnRaise, OnRecover = r.OnRecover, EscalateAfterMinutes = r.EscalateAfterMinutes
    };

    // 报警通知按报警所在区域、报表投递按报表区域判定权限。
    private sealed record FailedRow(NotificationOutbox Item, AlarmEvent? Alarm, ReportRun? Run)
    {
        public bool Can(AccessContext access) => Alarm is not null ? EndpointHelpers.CanAlarm(access, Permission.Configure, Alarm)
            : Run is not null ? access.Has(Permission.Configure, Run.AreaId) : access.HasGlobal(Permission.Configure);
        public string Message => Alarm?.Message ?? (Run is null ? "（对象已删除）" : $"报表 {Run.TemplateName} {Run.PeriodKey} v{Run.Version}");
    }
    private static async Task<List<FailedRow>> FailedAsync(IDbContextFactory<ConfigDbContext> factory, bool includeAcknowledged, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.NotificationOutbox.AsNoTracking().Where(x => x.Status == NotificationOutboxStatus.Failed);
        if (!includeAcknowledged) query = query.Where(x => x.AcknowledgedTs == null);
        var items = await query.OrderByDescending(x => x.Id).Take(500).ToListAsync(ct);
        var alarmIds = items.Where(x => x.AlarmEventId != null).Select(x => x.AlarmEventId!.Value).ToList();
        var runIds = items.Where(x => x.ReportRunId != null).Select(x => x.ReportRunId!.Value).ToList();
        var alarms = await db.AlarmEvents.AsNoTracking().Where(x => alarmIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var runs = await db.ReportRuns.AsNoTracking().Where(x => runIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return items.Select(x => new FailedRow(x, x.AlarmEventId is int a ? alarms.GetValueOrDefault(a) : null, x.ReportRunId is long r ? runs.GetValueOrDefault(r) : null)).ToList();
    }
}
