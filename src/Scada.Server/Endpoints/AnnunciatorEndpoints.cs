using Microsoft.EntityFrameworkCore;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public static class AnnunciatorEndpoints
{
    public static void MapAnnunciatorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/annunciators").AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("", async (HttpContext http, AnnunciatorService svc, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            bool visible(int? areaId) => areaId is null ? access.HasAny(Permission.View) : access.Has(Permission.View, areaId.Value);
            return Results.Ok(await svc.ListAsync(visible, ct));
        });

        group.MapGet("/status", async (HttpContext http, AnnunciatorService svc, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            bool visible(int? areaId) => areaId is null ? access.HasAny(Permission.View) : access.Has(Permission.View, areaId.Value);
            return Results.Ok(await svc.StatusAsync(visible, ct));
        });

        group.MapGet("/{id:int}", async (int id, HttpContext http, AnnunciatorService svc, CancellationToken ct) =>
        {
            var panel = await svc.GetAsync(id, ct);
            if (panel is null) return Results.NotFound();
            var access = await http.GetAccessAsync();
            if (panel.AreaId is int a ? !access.Has(Permission.View, a) : !access.HasAny(Permission.View)) return Results.NotFound();
            return Results.Ok(panel);
        });

        group.MapPost("", async (AnnunciatorPanelInput input, HttpContext http, AnnunciatorService svc, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (await RequireConfigureAsync(http, input.AreaId, $"新建声光面板 {input.Name}") is { } denied) return denied;
            if (await RequireOutputsAsync(http, factory, input, ct) is { } outputDenied) return outputDenied;
            var (dto, error) = await svc.UpsertAsync(null, input, http.User.GetUserName(), ct);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"新建声光面板 {dto!.Name}", "AnnunciatorPanel", dto.Id, null, dto));
            await db.SaveChangesAsync(ct);
            return Results.Ok(dto);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (int id, AnnunciatorPanelInput input, HttpContext http, AnnunciatorService svc, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            if (await RequireConfigureAsync(http, existing.AreaId, $"修改声光面板 {existing.Name}") is { } d1) return d1;
            if (await RequireConfigureAsync(http, input.AreaId, $"修改声光面板区域 {input.Name}") is { } d2) return d2;
            if (await RequireOutputsAsync(http, factory, input, ct) is { } outputDenied) return outputDenied;
            var (dto, error) = await svc.UpsertAsync(id, input, http.User.GetUserName(), ct);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"修改声光面板 {dto!.Name}", "AnnunciatorPanel", dto.Id, existing, dto));
            await db.SaveChangesAsync(ct);
            return Results.Ok(dto);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (int id, HttpContext http, AnnunciatorService svc, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            if (await RequireConfigureAsync(http, existing.AreaId, $"删除声光面板 {existing.Name}") is { } denied) return denied;
            var error = await svc.DeleteAsync(id, ct);
            if (error == "not found") return Results.NotFound();
            await using var db = await factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"删除声光面板 {existing.Name}", "AnnunciatorPanel", id, existing, null));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { success = true });
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/{id:int}/silence", async (int id, SilenceRequest? body, HttpContext http, AnnunciatorService svc, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            if (await RequireAckAsync(http, existing.AreaId, $"声光消音 {existing.Name}") is { } denied) return denied;
            var (status, error) = await svc.SilenceAsync(id, body?.Minutes, body?.Reason, http.User.GetUserName(), ct);
            if (error == "not found") return Results.NotFound();
            return Results.Ok(status);
        }).RequireAuthorization(AccessPolicies.AckAlarm);

        group.MapPost("/{id:int}/clear-silence", async (int id, HttpContext http, AnnunciatorService svc, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            if (await RequireAckAsync(http, existing.AreaId, $"取消声光消音 {existing.Name}") is { } denied) return denied;
            var (status, error) = await svc.ClearSilenceAsync(id, http.User.GetUserName(), ct);
            if (error == "not found") return Results.NotFound();
            return Results.Ok(status);
        }).RequireAuthorization(AccessPolicies.AckAlarm);

        group.MapPost("/{id:int}/test", async (int id, HttpContext http, AnnunciatorService svc, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null) return Results.NotFound();
            if (await RequireAckAsync(http, existing.AreaId, $"声光试验 {existing.Name}") is { } denied) return denied;
            var (status, error) = await svc.TestAsync(id, http.User.GetUserName(), ct);
            if (error == "not found") return Results.NotFound();
            return Results.Ok(status);
        }).RequireAuthorization(AccessPolicies.AckAlarm);

        group.MapPost("/ack-visible", async (AckVisibleRequest? body, HttpContext http, AlarmService alarms, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            if (!access.HasAny(Permission.AckAlarm)) return await Audit.ForbidAsync(http, "批量确认可见报警", "需要确认报警权限", "Alarm");
            var live = await alarms.QueryAsync(null, null, false, body?.Limit ?? 500, ct, unackedOnly: true, includeShelved: true, liveOnly: true);
            var targets = live.Where(x => EndpointHelpers.CanAlarm(access, Permission.AckAlarm, x)).ToList();
            var user = http.User.GetUserName();
            var acked = 0;
            foreach (var a in targets)
                if (await alarms.AckAsync(a.Id, user, body?.Note, ct) is not null) acked++;
            await using var db = await factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"批量确认可见报警 {acked} 条", "Alarm", null, null, new { acked, total = targets.Count }));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { acked, total = targets.Count });
        }).RequireAuthorization(AccessPolicies.AckAlarm);
    }

    private static async Task<IResult?> RequireConfigureAsync(HttpContext http, int? areaId, string action)
    {
        var access = await http.GetAccessAsync();
        if (areaId is int a) return access.Has(Permission.Configure, a) ? null : await Audit.ForbidAsync(http, action, $"无权配置区域 #{a}", "Area", a);
        return access.HasGlobal(Permission.Configure) ? null : await Audit.ForbidAsync(http, action, "全局声光面板需要全局配置权限", "AnnunciatorPanel");
    }

    // 声光输出由系统账号下发，不经过操作员的控制权限。配置时就必须能配置该测点所在区域，避免借面板写其他区域的设备。
    private static async Task<IResult?> RequireOutputsAsync(HttpContext http, IDbContextFactory<ConfigDbContext> factory, AnnunciatorPanelInput input, CancellationToken ct)
    {
        var access = await http.GetAccessAsync();
        await using var db = await factory.CreateDbContextAsync(ct);
        foreach (var output in input.Outputs ?? [])
        {
            var deviceId = await db.Tags.AsNoTracking().Where(x => x.Id == output.TagId).Select(x => x.DeviceId).FirstOrDefaultAsync(ct);
            if (deviceId is not int id) return await Audit.ForbidAsync(http, "配置声光输出", $"输出测点 #{output.TagId} 不存在或不是采集测点", "Tag", output.TagId);
            var areaId = await db.Devices.AsNoTracking().Where(x => x.Id == id).Select(x => (int?)x.AreaId).FirstOrDefaultAsync(ct);
            if (areaId is not int area || !access.Has(Permission.Configure, area))
                return await Audit.ForbidAsync(http, "配置声光输出", $"无权配置输出测点 #{output.TagId} 所在区域", "Tag", output.TagId);
        }
        return null;
    }

    private static async Task<IResult?> RequireAckAsync(HttpContext http, int? areaId, string action)
    {
        var access = await http.GetAccessAsync();
        if (areaId is int a) return access.Has(Permission.AckAlarm, a) ? null : await Audit.ForbidAsync(http, action, $"无权确认区域 #{a} 报警", "Area", a);
        return access.HasAny(Permission.AckAlarm) ? null : await Audit.ForbidAsync(http, action, "需要确认报警权限", "AnnunciatorPanel");
    }

    public sealed record SilenceRequest(int? Minutes, string? Reason);
    public sealed record AckVisibleRequest(string? Note, int? Limit);
}
