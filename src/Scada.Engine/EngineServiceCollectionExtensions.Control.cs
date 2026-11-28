using Microsoft.Extensions.DependencyInjection;
using Scada.Engine.Control;
using Scada.Core.Acquisition;
using Scada.Engine.Acquisition;
using Scada.Engine.Traceability;

namespace Scada.Engine;

public static partial class EngineServiceCollectionExtensions
{
    private static IServiceCollection AddScadaControl(this IServiceCollection services)
    {
        services.AddSingleton<ControlConfirmationStore>();
        services.AddSingleton<BatchConfirmationStore>();
        services.AddSingleton<ControlCommandService>();
        services.AddSingleton<MomentaryControlService>();
        services.AddSingleton<RecipeService>();
        services.AddSingleton<TraceabilityService>();
        services.AddSingleton<TraceabilityScannerHost>();
        services.AddSingleton<ControlWorkflowService>();
        services.AddSingleton<ProductionBatchService>();
        services.AddSingleton<DeviceAcquisitionPathService>();
        services.AddSingleton<IDeviceAcquisitionPaths>(sp => sp.GetRequiredService<DeviceAcquisitionPathService>());
        return services;
    }
}
