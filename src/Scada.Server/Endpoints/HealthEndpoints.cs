using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", Handle).AllowAnonymous();
        app.MapGet("/api/health", Handle).AllowAnonymous();
    }

    private static IResult Handle(HealthSnapshotService health) => Results.Ok(health.Snapshot());
}
