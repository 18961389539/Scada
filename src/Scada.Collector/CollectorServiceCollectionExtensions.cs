using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Interfaces;

namespace Scada.Collector;

public static class CollectorServiceCollectionExtensions
{
    public static IServiceCollection AddScadaCollector(this IServiceCollection services)
    {
        services.AddSingleton<IReadPlanBuilder, ReadPlanBuilder>();
        services.AddSingleton<IDeviceDriverFactory, Modbus.ModbusTcpDriverFactory>();
        services.AddSingleton<CollectorEngine>();
        services.AddSingleton<ICollectorControl>(sp => sp.GetRequiredService<CollectorEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<CollectorEngine>());
        return services;
    }
}
