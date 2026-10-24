using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;
using Scada.Engine.Alarms;

namespace Scada.Server.Endpoints;

public sealed record AlarmRuleRequest(
    int TagId,
    AlarmRuleType Type,
    double Threshold,
    AlarmLevel Level,
    int DelaySec,
    double RecoverDeadband,
    bool Enabled,
    string? Message,
    int OffDelaySec = 0,
    int? SuppressTagId = null,
    AlarmSuppressMode SuppressMode = AlarmSuppressMode.None);

public sealed record AlarmShelveRequest(int Minutes, string? Reason);
public sealed record AlarmUnshelveRequest(string? Reason);
public sealed record AlarmOutOfServiceRequest(bool OutOfService, string? Reason);

public static class AlarmRuleEndpoints
{
    public static void MapAlarmRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alarm-rules").AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("", async (
            int? tagId,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var query = db.AlarmRules.AsNoTracking();
            if (tagId is int id)
            {
                query = query.Where(x => x.TagId == id);
            }

            var access = await http.GetAccessAsync();
            var rules = await query.OrderBy(x => x.TagId).ThenBy(x => x.Type).ToListAsync(cancellationToken);
            return Results.Ok(rules.Where(x => access.CanTag(Permission.View, x.TagId)).ToList());
        });

        group.MapPost("", async (
            AlarmRuleRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointHelpers.RequireTagsAsync(http, [request.TagId], Permission.Configure, $"创建报警规则：测点 #{request.TagId}") is { } denied) return denied;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await db.Tags.AnyAsync(x => x.Id == request.TagId, cancellationToken))
            {
                return EndpointHelpers.BadRequest("测点不存在");
            }

            var rule = MapRule(request);
            var error = EndpointHelpers.ValidateAlarmRule(rule);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            if (await CheckSuppressTagAsync(http, db, rule, cancellationToken) is { } suppressError) return suppressError;

            db.AlarmRules.Add(rule);
            await db.SaveChangesAsync(cancellationToken);
            db.OperationLogs.Add(Audit.Change(http, $"创建报警规则：测点 #{rule.TagId}", "AlarmRule", rule.Id, null, rule));
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Created($"/api/alarm-rules/{rule.Id}", rule);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (
            int id,
            AlarmRuleRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var rule = await db.AlarmRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireTagsAsync(http, new[] { rule.TagId, request.TagId }.Distinct(), Permission.Configure, $"修改报警规则 #{id}") is { } denied) return denied;
            if (!await db.Tags.AnyAsync(x => x.Id == request.TagId, cancellationToken))
            {
                return EndpointHelpers.BadRequest("测点不存在");
            }

            var before = Copy(rule);
            var updated = MapRule(request);
            var error = EndpointHelpers.ValidateAlarmRule(updated);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            if (await CheckSuppressTagAsync(http, db, updated, cancellationToken) is { } suppressError) return suppressError;

            rule.TagId = updated.TagId;
            rule.Type = updated.Type;
            rule.Threshold = updated.Threshold;
            rule.Level = updated.Level;
            rule.DelaySec = updated.DelaySec;
            rule.RecoverDeadband = updated.RecoverDeadband;
            rule.Enabled = updated.Enabled;
            rule.Message = updated.Message;
            rule.OffDelaySec = updated.OffDelaySec;
            rule.SuppressTagId = updated.SuppressTagId;
            rule.SuppressMode = updated.SuppressMode;
            db.OperationLogs.Add(Audit.Change(http, $"修改报警规则 #{rule.Id}", "AlarmRule", rule.Id, before, rule));

            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(rule);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var rule = await db.AlarmRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (rule is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireTagsAsync(http, [rule.TagId], Permission.Configure, $"删除报警规则 #{id}") is { } denied) return denied;
            db.AlarmRules.Remove(rule);
            db.OperationLogs.Add(Audit.Change(http, $"删除报警规则 #{rule.Id}", "AlarmRule", rule.Id, rule, null));
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);

        // 停用（out of service）：维修/仪表故障期间不评估；必须写原因，走差分审计。
        group.MapPost("/{id:int}/out-of-service", async (
            int id,
            AlarmOutOfServiceRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var rule = await db.AlarmRules.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (rule is null) return Results.NotFound();
            var action = request.OutOfService ? $"停用报警规则 #{id}" : $"恢复报警规则 #{id}";
            if (await EndpointHelpers.RequireTagsAsync(http, [rule.TagId], Permission.Configure, action) is { } denied) return denied;
            if (request.OutOfService && string.IsNullOrWhiteSpace(request.Reason)) return EndpointHelpers.BadRequest("停用必须填写原因");
            if (request.Reason is { Length: > 500 }) return EndpointHelpers.BadRequest("原因不能超过 500 字");
            var before = Copy(rule);
            rule.OutOfService = request.OutOfService;
            rule.OutOfServiceReason = request.OutOfService ? request.Reason!.Trim() : null;
            db.OperationLogs.Add(Audit.Change(http, action, "AlarmRule", rule.Id, before, rule));
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(rule);
        }).RequireAuthorization(AccessPolicies.Configure);

        // 暂挂（shelve）：操作员动作，需报警所在区域的确认报警权限；限时、必须写原因。
        group.MapPost("/{id:int}/shelve", async (
            int id,
            AlarmShelveRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            AlarmShelfService shelf,
            Microsoft.Extensions.Options.IOptions<Scada.Core.Options.ScadaSettings> options,
            CancellationToken cancellationToken) =>
        {
            int tagId;
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                var rule = await db.AlarmRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (rule is null) return Results.NotFound();
                tagId = rule.TagId;
            }
            if (await EndpointHelpers.RequireTagsAsync(http, [tagId], Permission.AckAlarm, $"暂挂报警规则 #{id}") is { } denied) return denied;
            var result = await shelf.ShelveAsync(id, request.Minutes, request.Reason, http.User.GetUserName(), options.Value.AlarmShelveMaxMinutes, cancellationToken);
            if (!result.Success) return EndpointHelpers.BadRequest(result.Error!);
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                db.OperationLogs.Add(Audit.Change(http, $"暂挂报警规则 #{id}", "AlarmShelve", result.Shelve!.Id, null, result.Shelve));
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(result.Shelve);
        }).RequireAuthorization(AccessPolicies.AckAlarm);

        group.MapPost("/{id:int}/unshelve", async (
            int id,
            AlarmUnshelveRequest? request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            AlarmShelfService shelf,
            CancellationToken cancellationToken) =>
        {
            AlarmShelve? current;
            int tagId;
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                var rule = await db.AlarmRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (rule is null) return Results.NotFound();
                tagId = rule.TagId;
                current = await db.AlarmShelves.AsNoTracking().Where(x => x.RuleId == id && x.ClearedTs == null).OrderByDescending(x => x.StartTs).FirstOrDefaultAsync(cancellationToken);
            }
            if (await EndpointHelpers.RequireTagsAsync(http, [tagId], Permission.AckAlarm, $"解除暂挂 #{id}") is { } denied) return denied;
            var note = string.IsNullOrWhiteSpace(request?.Reason) ? "手动解除暂挂" : $"手动解除暂挂：{request!.Reason!.Trim()}";
            if (!await shelf.UnshelveAsync(id, http.User.GetUserName(), note, cancellationToken)) return EndpointHelpers.BadRequest("该规则未处于暂挂状态");
            await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
            {
                var after = await db.AlarmShelves.AsNoTracking().FirstOrDefaultAsync(x => x.Id == current!.Id, cancellationToken);
                db.OperationLogs.Add(Audit.Change(http, $"解除暂挂 #{id}", "AlarmShelve", current?.Id, current, after));
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Ok(new { success = true });
        }).RequireAuthorization(AccessPolicies.AckAlarm);
    }

    public static void MapAlarmShelveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/alarm-shelves", async (bool? history, HttpContext http, IDbContextFactory<ConfigDbContext> contextFactory, CancellationToken ct) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var query = db.AlarmShelves.AsNoTracking();
            if (history != true) query = query.Where(x => x.ClearedTs == null);
            var shelves = await query.OrderByDescending(x => x.StartTs).Take(1000).ToListAsync(ct);
            var ruleTags = await db.AlarmRules.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.TagId, ct);
            var access = await http.GetAccessAsync();
            return Results.Ok(shelves.Where(x => ruleTags.TryGetValue(x.RuleId, out var tag) && access.CanTag(Permission.View, tag))
                .Select(x => new { x.Id, x.RuleId, tagId = ruleTags[x.RuleId], x.Reason, x.User, x.StartTs, x.UntilTs, x.ClearedTs, x.ClearedBy }));
        }).RequireAuthorization(AccessPolicies.View);
    }

    private static async Task<IResult?> CheckSuppressTagAsync(HttpContext http, ConfigDbContext db, AlarmRule rule, CancellationToken ct)
    {
        if (rule.SuppressTagId is not int conditionId) return null;
        if (!await db.Tags.AnyAsync(x => x.Id == conditionId, ct)) return EndpointHelpers.BadRequest("条件测点不存在");
        // 条件测点至少要能查看，防止借抑制条件探测无权区域的测点。
        return await EndpointHelpers.RequireTagsAsync(http, [conditionId], Permission.View, $"报警规则引用条件测点 #{conditionId}");
    }

    private static AlarmRule Copy(AlarmRule r) => new()
    {
        Id = r.Id, TagId = r.TagId, Type = r.Type, Threshold = r.Threshold, Level = r.Level, DelaySec = r.DelaySec,
        RecoverDeadband = r.RecoverDeadband, Enabled = r.Enabled, Message = r.Message, OffDelaySec = r.OffDelaySec,
        SuppressTagId = r.SuppressTagId, SuppressMode = r.SuppressMode, OutOfService = r.OutOfService, OutOfServiceReason = r.OutOfServiceReason
    };

    private static AlarmRule MapRule(AlarmRuleRequest request) => new()
    {
        TagId = request.TagId,
        Type = request.Type,
        Threshold = request.Threshold,
        Level = request.Level,
        DelaySec = request.DelaySec,
        RecoverDeadband = request.RecoverDeadband,
        Enabled = request.Enabled,
        Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
        OffDelaySec = request.OffDelaySec,
        SuppressTagId = request.SuppressMode == AlarmSuppressMode.None ? null : request.SuppressTagId,
        SuppressMode = request.SuppressMode
    };
}
