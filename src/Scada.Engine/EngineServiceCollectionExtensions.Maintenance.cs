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
        services.AddSingleton(sp => new SpaceReclaimService(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Scada.Core.Options.ScadaSettings>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SpaceReclaimService>>(),
            sp.GetService<Scada.Data.Infrastructure.IHistoryDbFactory>(),
            () => sp.GetService<Scada.Engine.History.HistoryQueryService>()?.ActiveQueries ?? 0));
        services.AddSingleton<MaintenanceService>();
        // 时钟偏差是后台检测，必须显式注册成 HostedService；单例注册只为让健康聚合拿到同一个实例。
        services.AddSingleton<ClockSkewMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ClockSkewMonitor>());
        return services;
    }
}
