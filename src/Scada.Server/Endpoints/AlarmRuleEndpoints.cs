using Scada.Server.Auth;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record AlarmRuleRequest(
    int TagId,
    AlarmRuleType Type,
    double Threshold,
    AlarmLevel Level,
    int DelaySec,
    double RecoverDeadband,
    bool Enabled,
    string? Message);

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

            rule.TagId = updated.TagId;
            rule.Type = updated.Type;
            rule.Threshold = updated.Threshold;
            rule.Level = updated.Level;
            rule.DelaySec = updated.DelaySec;
            rule.RecoverDeadband = updated.RecoverDeadband;
            rule.Enabled = updated.Enabled;
            rule.Message = updated.Message;
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
    }

    private static AlarmRule Copy(AlarmRule r) => new()
    {
        Id = r.Id, TagId = r.TagId, Type = r.Type, Threshold = r.Threshold, Level = r.Level, DelaySec = r.DelaySec,
        RecoverDeadband = r.RecoverDeadband, Enabled = r.Enabled, Message = r.Message
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
        Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim()
    };
}
