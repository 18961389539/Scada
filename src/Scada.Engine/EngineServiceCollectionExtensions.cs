using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.Alarms;
using Scada.Engine.Aggregation;
using Scada.Engine.Calculations;
using Scada.Engine.Control;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Engine.Notifications;
using Scada.Engine.Oee;
using Scada.Engine.Realtime;
using Scada.Engine.Reports;
using Scada.Engine.Spc;
using Scada.Engine.Traceability;

namespace Scada.Engine;

/// <summary>
/// Engine 的组装入口。注册按域拆到同目录的 partial 文件：
/// History / Alarms / Oee / Control / Calculations / Maintenance。
/// 各 <c>AddScada*</c> 只注册单例，后台服务的启动顺序集中在 <see cref="AddScadaEngineHostedServices"/>，
/// 这样"某个域的注册被移动"不会悄悄改变启动顺序。
/// </summary>
public static partial class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddScadaEngine(this IServiceCollection services)
    {
        services.AddScadaEngineCore();
        services.AddScadaHistory();
        services.AddScadaAlarms();
        services.AddScadaOee();
        services.AddScadaControl();
        services.AddScadaCalculations();
        services.AddScadaMaintenance();
        services.AddScadaEngineHostedServices();
        return services;
    }

    private static IServiceCollection AddScadaEngineCore(this IServiceCollection services)
    {
        services.TryAddSingleton<INodeRole, StandaloneNodeRole>();
        services.TryAddSingleton<LivenessRegistry>();
        services.AddSingleton<WatchdogService>();

        services.AddSingleton<RealtimeCache>();
        services.AddSingleton<IRealtimeCache>(sp => sp.GetRequiredService<RealtimeCache>());
        // 历史反压等待默认 0（非阻塞）：发布方是采集与计算线程，不该为历史落盘让步。
        services.AddSingleton(sp => new RealtimeBus(TimeSpan.FromMilliseconds(
            Math.Max(0, sp.GetRequiredService<IOptions<ScadaSettings>>().Value.RealtimeBusHistoryWaitMs))));
        services.AddSingleton<IRealtimeBus>(sp => sp.GetRequiredService<RealtimeBus>());
        services.AddSingleton<ReportService>();
        return services;
    }

    /// <summary>
    /// 后台服务的启动顺序 = 这里的注册顺序。新增服务请追加到合适位置，不要插到中间。
    /// 全部走"单例 + AddHostedService(工厂)"：既能被其他组件注入读取状态，
    /// 也避免 <c>AddHostedService&lt;T&gt;()</c> 把同一个类型登记成两个独立实例。
    /// </summary>
    private static IServiceCollection AddScadaEngineHostedServices(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<WatchdogService>());
        services.AddHostedService(sp => sp.GetRequiredService<BackupSchedulerService>());
        services.AddHostedService(sp => sp.GetRequiredService<AlarmWebhookNotifier>());
        services.AddHostedService(sp => sp.GetRequiredService<SystemWatch>());
        services.AddHostedService(sp => sp.GetRequiredService<AnnunciatorService>());
        services.AddHostedService(sp => sp.GetRequiredService<AlarmRollupService>());
        services.AddHostedService(sp => sp.GetRequiredService<OeeCollectorService>());
        services.AddHostedService(sp => sp.GetRequiredService<OeeRollupService>());
        services.AddHostedService(sp => sp.GetRequiredService<SpcMonitorService>());
        services.AddHostedService(sp => sp.GetRequiredService<MomentaryControlService>());
        services.AddHostedService(sp => sp.GetRequiredService<RecipeService>());
        services.AddHostedService(sp => sp.GetRequiredService<TraceabilityService>());
        services.AddHostedService(sp => sp.GetRequiredService<TraceabilityScannerHost>());
        services.AddHostedService(sp => sp.GetRequiredService<ControlWorkflowService>());
        services.AddHostedService(sp => sp.GetRequiredService<ProductionBatchService>());
        services.AddHostedService(sp => sp.GetRequiredService<HistoryWriterService>());
        services.AddHostedService(sp => sp.GetRequiredService<AlarmEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<CalculatedTagEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<AggregationService>());
        services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());
        return services;
    }
}
