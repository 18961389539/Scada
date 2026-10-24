using Scada.Engine.Maintenance;

namespace Scada.Server.Endpoints;

public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/maintenance/backups").RequireAuthorization("Admin");

        group.MapGet("", (BackupService backups) => Results.Ok(backups.List()));

        group.MapPost("", async (BackupService backups, CancellationToken cancellationToken) =>
            Results.Ok(await backups.CreateAsync(cancellationToken)));

        group.MapPost("/{fileName}/verify", async (string fileName, BackupService backups, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await backups.VerifyAsync(fileName, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return EndpointHelpers.BadRequest(ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });

        group.MapGet("/{fileName}", (string fileName, BackupService backups) =>
        {
            try
            {
                var path = backups.Resolve(fileName);
                return Results.File(path, "application/zip", fileName);
            }
            catch (ArgumentException ex)
            {
                return EndpointHelpers.BadRequest(ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
        });
    }
}
