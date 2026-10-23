using Scada.Engine.Control;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record CommandRequest(int TagId, double Value);

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
            if (request.TagId <= 0)
            {
                return EndpointHelpers.BadRequest("请指定测点");
            }

            var result = await commandService.ExecuteAsync(
                request.TagId,
                request.Value,
                http.User.GetUserId(),
                http.User.GetUserName(),
                cancellationToken);

            return Results.Ok(new
            {
                success = result.Success,
                message = result.Message,
                readBack = result.ReadBack
            });
        }).RequireAuthorization("Operator");
    }
}
