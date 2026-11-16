using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scada.Collector.Drivers;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Opc;
using Scada.Core.Simulation;

namespace Scada.Collector.Tests;

public class CollectorServiceCollectionExtensionsTests
{
    [Fact]
    public void AddScadaCollectorRegistersEveryCollectorContract()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scada:ConfigDbPath"] = "data/scada.db" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScadaCollector(configuration);

        Assert.Contains(services, d => d.ServiceType == typeof(IReadPlanBuilder) && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d => d.ServiceType == typeof(Simulation.SimulationRuntime));
        Assert.Contains(services, d => d.ServiceType == typeof(ISimulationRuntime));
        Assert.Contains(services, d => d.ServiceType == typeof(Opc.OpcCertificateService));
        Assert.Contains(services, d => d.ServiceType == typeof(IOpcCertificateService));
        Assert.Contains(services, d => d.ServiceType == typeof(DriverPluginCatalog));
        Assert.Contains(services, d => d.ServiceType == typeof(IProtocolDriverAvailability));
        Assert.Contains(services, d => d.ServiceType == typeof(IDeviceDriverFactory));
        Assert.Contains(services, d => d.ServiceType == typeof(Opc.OpcUaRuntime));
        Assert.Contains(services, d => d.ServiceType == typeof(CollectorEngine));
        Assert.Contains(services, d => d.ServiceType == typeof(ILocalCollector));
        Assert.Contains(services, d => d.ServiceType == typeof(ICollectorControl));
        Assert.Contains(services, d => d.ServiceType == typeof(ICollectorDiagnostics));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddScadaCollectorWithoutConfigurationUsesDefaults()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScadaCollector(new ConfigurationBuilder().Build());
        Assert.Contains(services, d => d.ServiceType == typeof(CollectorEngine));
    }
}