using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.History;

namespace Scada.Engine;

public static partial class EngineServiceCollectionExtensions
{
    private static IServiceCollection AddScadaHistory(this IServiceCollection services)
    {
        // INodeRole is resolved lazily: HaCoordinator implements it and depends on IHistoryBackend,
        // so injecting the role into HistoryStore here would form a constructor cycle.
        services.AddSingleton<HistoryStore>(sp => new HistoryStore(
            sp.GetRequiredService<Scada.Data.Infrastructure.IHistoryDbFactory>(),
            sp.GetRequiredService<IOptions<ScadaSettings>>(),
            sp.GetService<ILogger<SqliteHistoryBackend>>(),
            role: null,
            services: sp));
        services.AddSingleton<IHistoryBackend>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddSingleton<IHistoryStore>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddSingleton<IGatewayHistoryWriter>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddSingleton<IHistoryStreamReader>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddSingleton(sp =>
        {
            var s = sp.GetRequiredService<IOptions<ScadaSettings>>().Value;
            Directory.CreateDirectory(s.HistoryRoot);
            var path = Path.Combine(s.HistoryRoot, HistoryLossLog.FileName);
            return new HistoryLossLog(path, () => DateTimeOffset.UtcNow.AddMonths(-Math.Max(s.HistoryRetentionMonths, 1)).ToUnixTimeMilliseconds());
        });
        services.AddSingleton<HistoryQueryService>();
        services.AddSingleton(sp =>
        {
            var s = sp.GetRequiredService<IOptions<ScadaSettings>>().Value;
            return new HistorySpool(s.HistoryRoot, s.HistorySpoolMaxBytes, s.HistorySpoolSegmentBytes,
                s.HistorySpoolMinFreeMb, s.HistorySpoolDropOldestOnFull, lossLog: sp.GetRequiredService<HistoryLossLog>());
        });
        services.AddSingleton<HistoryWriterService>();
        return services;
    }
}
