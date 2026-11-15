using Microsoft.Extensions.DependencyInjection;
using Scada.Engine.Oee;
using Scada.Engine.Spc;

namespace Scada.Engine;

public static partial class EngineServiceCollectionExtensions
{
    private static IServiceCollection AddScadaOee(this IServiceCollection services)
    {
        services.AddSingleton<OeeService>();
        services.AddSingleton<OeeCollectorService>();
        services.AddSingleton<OeeRollupService>();
        services.AddSingleton<SpcMonitorService>();
        return services;
    }
}
