using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;
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
        var group = app.MapGroup("/api/alarm-rules");

        group.MapGet("", async (
            int? tagId,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var query = db.AlarmRules.AsNoTracking();
            if (tagId is int id)
            {
                query = query.Where(x => x.TagId == id);
            }

            return Results.Ok(await query.OrderBy(x => x.TagId).ThenBy(x => x.Type).ToListAsync(cancellationToken));
        });

        group.MapPost("", async (
            AlarmRuleRequest request,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
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
            await reloader.ReloadAsync(cancellationToken);
            return Results.Created($"/api/alarm-rules/{rule.Id}", rule);
        }).RequireAuthorization("Admin");

        group.MapPut("/{id:int}", async (
            int id,
            AlarmRuleRequest request,
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

            if (!await db.Tags.AnyAsync(x => x.Id == request.TagId, cancellationToken))
            {
                return EndpointHelpers.BadRequest("测点不存在");
            }

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

            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(rule);
        }).RequireAuthorization("Admin");

        group.MapDelete("/{id:int}", async (
            int id,
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

            db.AlarmRules.Remove(rule);
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Admin");
    }

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
