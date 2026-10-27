using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Interfaces;
using Opc.Ua.Client;
using Scada.Core.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace Scada.Collector;

public static class CollectorServiceCollectionExtensions
{
    public static IServiceCollection AddScadaCollector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IReadPlanBuilder, ReadPlanBuilder>();
        services.AddSingleton<IDeviceDriverFactory, DeviceDriverFactory>();
        var settings = configuration.GetSection("Scada").Get<ScadaSettings>() ?? new ScadaSettings();
        services.AddOpcUa().AddClient(options =>
        {
            options.ApplicationName = "ScadaCollector";
            var machineId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName)))[..24];
            options.ApplicationUri = $"urn:scada:collector:{machineId}";
            options.ProductUri = "urn:scada:collector";
            options.PkiRoot = Path.GetFullPath(settings.OpcPkiRoot ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.ConfigDbPath))!, "opc-pki"));
            options.AutoAcceptUntrustedCertificates = false; options.RejectSHA1SignedCertificates = true; options.MinimumCertificateKeySize = 2048;
        });
        services.AddSingleton<Opc.OpcUaRuntime>();
        services.AddSingleton<CollectorEngine>();
        services.AddSingleton<ICollectorControl>(sp => sp.GetRequiredService<CollectorEngine>());
        services.AddSingleton<ICollectorDiagnostics>(sp => sp.GetRequiredService<CollectorEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<CollectorEngine>());
        return services;
    }
}