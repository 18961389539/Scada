using Scada.Core.Security;
using Scada.Engine.Control;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record CommandRequest(int TagId, double Value, Guid? RequestId = null, bool Confirmed = false);

public static class CommandEndpoints
{
    public static void MapCommandEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/commands", async (
            CommandRequest request,
            HttpContext http,
            ControlCommandService commandService,
            CancellationToken cancellationToken) =>
        {
            if (request.TagId <= 0 || request.RequestId == Guid.Empty)
            {
                return EndpointHelpers.BadRequest("请指定测点");
            }

            if (!(await http.GetAccessAsync()).CanTag(Permission.Control, request.TagId))
                return await Audit.ForbidAsync(http, $"控制命令：测点 #{request.TagId} = {request.Value}", "无权控制该区域的测点", "Tag", request.TagId);

            var result = await commandService.SubmitAsync(new ControlSubmission(request.TagId, request.Value, ControlEndpoints.Actor(http), ControlOrigin.Operator,
                "实时监视", request.Confirmed, request.RequestId), cancellationToken);

            return Results.Ok(new
            {
                success = result.Success,
                message = result.Message,
                readBack = result.ReadBack,
                commandId = result.CommandId, outcome = result.Outcome,
                pending = result.Pending, approvalId = result.ApprovalId, refusals = result.Refusals
            });
        }).RequireAuthorization(AccessPolicies.Control);
        app.MapGet("/api/commands/{id:guid}", async (Guid id, HttpContext http, Scada.Server.Services.GatewayCenterService gateways, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var status = await gateways.CommandStatusAsync(id, http.User.GetUserId(), (await http.GetAccessAsync()).HasGlobal(Permission.Configure), ct);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).RequireAuthorization(AccessPolicies.Control);
    }
}
