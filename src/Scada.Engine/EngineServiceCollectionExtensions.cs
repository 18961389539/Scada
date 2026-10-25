using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.Alarms;
using Scada.Engine.Aggregation;
using Scada.Engine.Control;
using Scada.Engine.History;
using Scada.Engine.Notifications;
using Scada.Engine.Maintenance;
using Scada.Engine.Realtime;
using Scada.Engine.Oee;
using Scada.Engine.Reports;

namespace Scada.Engine;

public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddScadaEngine(this IServiceCollection services)
    {
        services.TryAddSingleton<INodeRole, StandaloneNodeRole>();
        services.TryAddSingleton<LivenessRegistry>();
        services.AddHostedService<WatchdogService>();
        services.AddSingleton<RealtimeCache>();
        services.AddSingleton<IRealtimeCache>(sp => sp.GetRequiredService<RealtimeCache>());
        services.AddSingleton<RealtimeBus>();
        services.AddSingleton<IRealtimeBus>(sp => sp.GetRequiredService<RealtimeBus>());

        services.AddSingleton<HistoryStore>();
        services.AddSingleton<IHistoryStore>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddSingleton<IGatewayHistoryWriter>(sp => sp.GetRequiredService<HistoryStore>());
        services.AddSingleton<HistoryQueryService>();
        services.AddSingleton<HistorySpool>(sp =>
        {
            var s = sp.GetRequiredService<IOptions<ScadaSettings>>().Value;
            return new HistorySpool(s.HistoryRoot, s.HistorySpoolMaxBytes, s.HistorySpoolSegmentBytes, s.HistorySpoolMinFreeMb, s.HistorySpoolDropOldestOnFull);
        });
        services.AddSingleton<BackupService>();
        services.AddSingleton<DiagnosticService>();
        services.AddSingleton<BackupScheduler>(sp => new BackupScheduler(sp.GetRequiredService<BackupService>(), sp.GetRequiredService<IOptions<ScadaSettings>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackupScheduler>>(), sp.GetService<INodeRole>()));
        services.AddHostedService(sp => sp.GetRequiredService<BackupScheduler>());
        services.AddSingleton<AlarmWebhookNotifier>();
        services.AddSingleton<IAlarmWebhookStatus>(sp => sp.GetRequiredService<AlarmWebhookNotifier>());
        services.AddHostedService(sp => sp.GetRequiredService<AlarmWebhookNotifier>());
        services.AddSingleton<SystemWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<SystemWatch>());
        services.AddSingleton<ReportService>();
        services.AddSingleton<AlarmService>();
        services.AddSingleton<AlarmShelfService>();
        services.AddSingleton<AlarmKpiService>();
        services.AddSingleton<AlarmAnalyticsService>();
        services.AddSingleton<AlarmRollupService>();
        services.AddSingleton<AnnunciatorService>();
        services.AddHostedService(sp => sp.GetRequiredService<AnnunciatorService>());
        services.AddHostedService(sp => sp.GetRequiredService<AlarmRollupService>());
        services.AddSingleton<OeeService>();
        services.AddSingleton<OeeCollectorService>();
        services.AddHostedService(sp => sp.GetRequiredService<OeeCollectorService>());
        services.AddSingleton<OeeRollupService>();
        services.AddHostedService(sp => sp.GetRequiredService<OeeRollupService>());
        services.AddSingleton<IAlarmSink, AlarmSink>();
        services.AddSingleton<IAlarmNotifier, NullAlarmNotifier>();
        services.AddSingleton<ControlCommandService>();
        services.AddSingleton<RecipeService>();
        services.AddHostedService(sp => sp.GetRequiredService<RecipeService>());
        services.AddSingleton<ControlWorkflowService>();
        services.AddHostedService(sp => sp.GetRequiredService<ControlWorkflowService>());
        services.AddSingleton<Scada.Engine.Calculations.CalculatedTagConfiguration>();

        services.AddHostedService<HistoryWriterService>();
        services.AddHostedService<AlarmEngine>();
        services.AddSingleton<Scada.Engine.Calculations.CalculatedTagEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<Scada.Engine.Calculations.CalculatedTagEngine>());
        services.AddHostedService<AggregationService>();
        services.AddHostedService<MaintenanceService>();
        return services;
    }
}
