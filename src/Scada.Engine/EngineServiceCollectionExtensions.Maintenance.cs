using Microsoft.Extensions.DependencyInjection;
using Scada.Engine.Maintenance;

namespace Scada.Engine;

public static partial class EngineServiceCollectionExtensions
{
    private static IServiceCollection AddScadaMaintenance(this IServiceCollection services)
    {
        services.AddSingleton<BackupService>();
        services.AddSingleton<DiagnosticService>();
        services.AddSingleton<BackupScheduler>();
        services.AddSingleton<BackupSchedulerService>();
        services.AddSingleton<SystemWatch>();
        services.AddSingleton<MaintenanceService>();
        return services;
    }
}
