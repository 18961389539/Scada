using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Control;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record ProductionBatchCheckRequest(int RecipeId, int Version, int? SequenceId);
public sealed record ProductionBatchStartRequest(string BatchNumber, int RecipeId, int Version, int? SequenceId, bool Confirmed, string? Challenge = null);

public static class ProductionBatchEndpoints
{
    public static void MapProductionBatchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/production-batches").RequireAuthorization().AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("/", async (string? batchNumber, int? before, HttpContext http, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var query = runtime.ProductionBatches.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(batchNumber))
            {
                var key = batchNumber.Trim();
                query = query.Where(x => x.BatchNumber.Contains(key));
            }
            if (access.AreasWith(Permission.View) is { } areas) { var allowed = areas.ToArray(); query = query.Where(x => allowed.Contains(x.AreaId)); }
            if (before is int cursor) query = query.Where(x => x.Id < cursor);
            var rows = await query.OrderByDescending(x => x.Id).Take(201).ToListAsync(ct);
            var more = rows.Count > 200;
            if (more) rows.RemoveAt(rows.Count - 1);
            EndpointHelpers.MarkPage(http, more, rows.Count > 0 ? rows[^1].Id : null);
            return Results.Ok(rows.Select(ListView));
        });

        group.MapGet("/{id:int}", async (int id, HttpContext http, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var batch = await runtime.ProductionBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (batch is null || !(await http.GetAccessAsync()).Has(Permission.View, batch.AreaId)) return Results.NotFound();
            RecipeDownload? download = null;
            ControlSequenceRun? run = null;
            ControlRequest? request = null;
            if (batch.RecipeDownloadId is int did)
                download = await runtime.RecipeDownloads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == did, ct);
            if (batch.ControlSequenceRunId is int rid)
                run = await runtime.ControlSequenceRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rid, ct);
            if (batch.SequenceRequestId is Guid qid)
                request = await runtime.ControlRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == qid, ct);

            var ids = new List<string> { batch.Id.ToString(CultureInfo.InvariantCulture) };
            if (batch.RecipeDownloadId is int d) ids.Add(d.ToString(CultureInfo.InvariantCulture));
            if (batch.ControlSequenceRunId is int r) ids.Add(r.ToString(CultureInfo.InvariantCulture));
            var types = new[] { "ProductionBatch", "RecipeDownload", "ControlSequenceRun", "ControlRequest" };
            var audits = await runtime.OperationLogs.AsNoTracking()
                .Where(x => types.Contains(x.TargetType) && x.TargetId != null && ids.Contains(x.TargetId))
                .OrderBy(x => x.Ts)
                .Take(200)
                .Select(x => new { x.Ts, x.UserName, x.Action, x.Result, x.Details, x.TargetType })
                .ToListAsync(ct);
            return Results.Ok(DetailView(batch, download, run, request, audits));
        });

        group.MapPost("/check", async (ProductionBatchCheckRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, RecipeService recipes, ControlWorkflowService workflow, BatchConfirmationStore confirmations, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            try
            {
                var (template, recipe, version, sequence, denied) = await LoadAndAuthorizeAsync(http, db, request.RecipeId, request.Version, request.SequenceId, ct);
                if (denied is not null) return await Audit.ForbidAsync(http, $"预检查批次 {recipe.Name}", denied, "Recipe", recipe.Id);
                var check = await recipes.CheckAsync(template, recipe, version, ControlEndpoints.Actor(http), ct);
                var refusals = check.Refusals.ToList();
                var dual = check.DualApproval;
                if (sequence is not null)
                {
                    var preflight = await workflow.CheckStartAsync(sequence, ControlEndpoints.Actor(http), ct);
                    if (!preflight.Ok) refusals.AddRange(preflight.Refusals);
                    dual = dual || sequence.DualApproval;
                }
                var grant = confirmations.Grant(ControlEndpoints.Actor(http).UserId, "production-batch", recipe.Id, Fingerprint(template, recipe, version, sequence));
                return Results.Ok(new
                {
                    recipeId = recipe.Id, version = version.Version, sequenceId = sequence?.Id, sequenceName = sequence?.Name,
                    areaId = template.AreaId, recipeName = recipe.Name, dualApproval = dual,
                    ok = refusals.Count == 0, refusals,
                    check.Parameters,
                    confirmationChallenge = grant.Challenge, confirmationExpiresTs = grant.ExpiresTs
                });
            }
            catch (ControlWorkflowException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.Status); }
        }).RequireAuthorization(AccessPolicies.Control);

        group.MapPost("/", async (ProductionBatchStartRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, ProductionBatchService batches, BatchConfirmationStore confirmations, CancellationToken ct) =>
        {
            var number = request.BatchNumber?.Trim() ?? "";
            if (number.Length is 0 or > 100) return EndpointHelpers.BadRequest("批号须为 1～100 个字符");
            if (request.RecipeId <= 0) return EndpointHelpers.BadRequest("批次必须绑定配方");
            await using var db = await factory.CreateDbContextAsync(ct);
            try
            {
                var (template, recipe, version, sequence, denied) = await LoadAndAuthorizeAsync(http, db, request.RecipeId, request.Version, request.SequenceId, ct);
                if (denied is not null) return await Audit.ForbidAsync(http, $"开始批次 {number}", denied, "Recipe", recipe.Id);
                if (!request.Confirmed) return EndpointHelpers.BadRequest("开始批次前须确认");
                if (!confirmations.TryConsume(ControlEndpoints.Actor(http).UserId, "production-batch", recipe.Id, Fingerprint(template, recipe, version, sequence), request.Challenge))
                    return EndpointHelpers.BadRequest("开始批次前须先调用检查接口并在确认框中确认");
                var batch = await batches.StartAsync(number, recipe.Id, version.Version, sequence?.Id, ControlEndpoints.Actor(http), ct);
                return Results.Ok(ListView(batch));
            }
            catch (ControlWorkflowException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.Status); }
        }).RequireAuthorization(AccessPolicies.Control);

        group.MapPost("/{id:int}/abort", async (int id, HttpContext http, IDbContextFactory<RuntimeDbContext> runtimeFactory, ProductionBatchService batches, CancellationToken ct) =>
        {
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var batch = await runtime.ProductionBatches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (batch is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Control, batch.AreaId))
                return await Audit.ForbidAsync(http, $"中止批次 {batch.BatchNumber}", "无该区域的控制权限", "ProductionBatch", id);
            return await batches.AbortAsync(id, ControlEndpoints.Actor(http), ct) ? Results.Accepted() : Results.Conflict(new { message = "批次已结束" });
        }).RequireAuthorization(AccessPolicies.Control);
    }

    private static async Task<(RecipeTemplate Template, Recipe Recipe, RecipeVersion Version, ControlSequence? Sequence, string? Denied)> LoadAndAuthorizeAsync(
        HttpContext http, ConfigDbContext db, int recipeId, int version, int? sequenceId, CancellationToken ct)
    {
        var (template, recipe, ver) = await RecipeService.LoadAsync(db, recipeId, version, ct);
        var denied = await RecipeEndpoints.CanDownloadAsync(http, template);
        ControlSequence? sequence = null;
        if (denied is null && sequenceId is int sid)
        {
            sequence = await db.ControlSequences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sid, ct)
                ?? throw new ControlWorkflowException("顺控不存在", 404);
            denied = await ControlEndpoints.CanRunAsync(http, sequence);
        }
        return (template, recipe, ver, sequence, denied);
    }

    private static string Fingerprint(RecipeTemplate template, Recipe recipe, RecipeVersion version, ControlSequence? sequence) =>
        BatchConfirmationStore.Fingerprint(
            recipe.Id.ToString(CultureInfo.InvariantCulture),
            version.Version.ToString(CultureInfo.InvariantCulture),
            template.Revision.ToString(CultureInfo.InvariantCulture),
            (sequence?.Id ?? 0).ToString(CultureInfo.InvariantCulture),
            (sequence?.Revision ?? 0).ToString(CultureInfo.InvariantCulture));

    public static object ListView(ProductionBatch x) => new
    {
        x.Id, x.BatchNumber, x.AreaId, x.RecipeId, x.RecipeName, x.RecipeVersion,
        x.SequenceId, x.SequenceName, x.RecipeDownloadId, x.ControlSequenceRunId, x.SequenceRequestId,
        status = x.Status.ToString(), statusLabel = ProductionBatchService.Label(x.Status),
        x.StartedTs, x.EndedTs, x.StartedBy, x.Message
    };

    private static object DetailView(ProductionBatch batch, RecipeDownload? download, ControlSequenceRun? run, ControlRequest? request, object audits) => new
    {
        batch = ListView(batch),
        download = download is null ? null : RecipeEndpoints.DownloadView(download),
        sequenceRun = run is null ? null : new
        {
            run.Id, run.SequenceId, run.SequenceName, run.AreaId, status = run.Status.ToString(),
            run.StartedTs, run.EndedTs, run.StartedBy, run.ApprovedBy, run.Message,
            steps = JsonSerializer.Deserialize<List<ControlStepResult>>(run.StepsJson, ControlWorkflowService.Json)
        },
        sequenceRequest = request is null ? null : new { request.Id, status = request.Status.ToString(), request.RequestedBy, request.DecidedBy, request.Comment, request.RunId },
        audits
    };
}
