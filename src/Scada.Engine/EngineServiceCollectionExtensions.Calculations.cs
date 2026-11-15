using Microsoft.Extensions.DependencyInjection;
using Scada.Engine.Aggregation;
using Scada.Engine.Calculations;

namespace Scada.Engine;

public static partial class EngineServiceCollectionExtensions
{
    private static IServiceCollection AddScadaCalculations(this IServiceCollection services)
    {
        services.AddSingleton<CalculatedTagConfiguration>();
        services.AddSingleton<CalculatedTagEngine>();
        services.AddSingleton<AggregationService>();
        return services;
    }
}
