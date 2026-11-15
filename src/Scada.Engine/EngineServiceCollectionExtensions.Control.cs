using Microsoft.Extensions.DependencyInjection;
using Scada.Engine.Control;
using Scada.Engine.Traceability;

namespace Scada.Engine;

public static partial class EngineServiceCollectionExtensions
{
    private static IServiceCollection AddScadaControl(this IServiceCollection services)
    {
        services.AddSingleton<ControlCommandService>();
        services.AddSingleton<MomentaryControlService>();
        services.AddSingleton<RecipeService>();
        services.AddSingleton<TraceabilityService>();
        services.AddSingleton<TraceabilityScannerHost>();
        services.AddSingleton<ControlWorkflowService>();
        return services;
    }
}
