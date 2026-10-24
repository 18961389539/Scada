using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.History;
using Scada.Engine.Maintenance;

namespace Scada.Server.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", Handle).AllowAnonymous();
        app.MapGet("/api/health", Handle).AllowAnonymous();
    }

    private static IResult Handle(
        ICollectorControl collector,
        IRealtimeBus bus,
        IConfigSnapshot config,
        HistorySpool spool,
        SystemWatch watch,
        IOptions<ScadaSettings> settings,
        IHostEnvironment environment)
    {
            var statuses = collector.GetStatuses();
            var online = statuses.Count(x => x.Online);
            var configPath = Path.GetFullPath(settings.Value.ConfigDbPath);
            var dataRoot = Path.GetPathRoot(configPath) ?? configPath;

            double totalGb = 0;
            double freeGb = 0;
            try
            {
                var drive = new DriveInfo(dataRoot);
                totalGb = Math.Round(drive.TotalSize / 1024d / 1024d / 1024d, 2);
                freeGb = Math.Round(drive.AvailableFreeSpace / 1024d / 1024d / 1024d, 2);
            }
            catch
            {
                // drive info unavailable
            }

            long historyBytes = 0;
            if (Directory.Exists(settings.Value.HistoryRoot))
            {
                historyBytes = Directory.EnumerateFiles(settings.Value.HistoryRoot, "*.db")
                    .Sum(file => new FileInfo(file).Length);
            }

            var status = statuses.Count > 0 && online == 0 ? "degraded" : "ok";
            var warnings = watch.Current.Warnings;
            if (warnings.Count > 0)
            {
                status = "degraded";
            }

            return Results.Ok(new
            {
                status,
                warnings,
                environment = environment.EnvironmentName,
                serverTime = DateTimeOffset.UtcNow,
                uptimeSeconds = Environment.TickCount64 / 1000,
                devices = statuses,
                onlineDevices = online,
                totalDevices = statuses.Count,
                configuredTags = config.Tags.Count,
                busBacklog = bus.Backlog,
                busDropped = bus.DroppedTotal,
                historySpool = new
                {
                    pendingBytes = spool.PendingBytes,
                    spilledBatches = spool.SpilledBatches,
                    rejectedBatches = spool.DroppedBatches
                },
                disk = new
                {
                    root = dataRoot,
                    totalGb,
                    freeGb
                },
                storage = new
                {
                    configDb = Path.GetFileName(configPath),
                    configDbBytes = File.Exists(configPath) ? new FileInfo(configPath).Length : 0,
                    historyBytes
                }
            });
    }
}
