using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Interfaces;
using Scada.Engine.Alarms;
using Scada.Engine.Aggregation;
using Scada.Engine.Control;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Engine.Realtime;
using Scada.Engine.Reports;

namespace Scada.Engine;

public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddScadaEngine(this IServiceCollection services)
    {
        services.AddSingleton<RealtimeCache>();
        services.AddSingleton<IRealtimeCache>(sp => sp.GetRequiredService<RealtimeCache>());
        services.AddSingleton<RealtimeBus>();
        services.AddSingleton<IRealtimeBus>(sp => sp.GetRequiredService<RealtimeBus>());

        services.AddSingleton<IHistoryStore, HistoryStore>();
        services.AddSingleton<HistoryQueryService>();
        services.AddSingleton<ReportService>();
        services.AddSingleton<AlarmService>();
        services.AddSingleton<IAlarmSink, AlarmSink>();
        services.AddSingleton<IAlarmNotifier, NullAlarmNotifier>();
        services.AddSingleton<ControlCommandService>();

        services.AddHostedService<HistoryWriterService>();
        services.AddHostedService<AlarmEngine>();
        services.AddHostedService<AggregationService>();
        services.AddHostedService<MaintenanceService>();
        return services;
    }
}
