using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.Alarms;
using Scada.Engine.Notifications;

namespace Scada.Engine;

public static partial class EngineServiceCollectionExtensions
{
    private static IServiceCollection AddScadaAlarms(this IServiceCollection services)
    {
        services.AddSingleton<AlarmWebhookNotifier>();
        services.AddSingleton<IAlarmWebhookStatus>(sp => sp.GetRequiredService<AlarmWebhookNotifier>());
        services.AddSingleton<AlarmService>();
        services.AddSingleton<AlarmShelfService>();
        services.AddSingleton(sp => new AlarmFloodGuard(sp.GetRequiredService<IOptions<ScadaSettings>>().Value));
        services.AddSingleton<AlarmKpiService>();
        services.AddSingleton<AlarmAnalyticsService>();
        services.AddSingleton<AlarmRollupService>();
        services.AddSingleton<AnnunciatorService>();
        services.AddSingleton<IAlarmSink, AlarmSink>();
        services.AddSingleton<IAlarmNotifier, NullAlarmNotifier>();

        // 报警判定引擎。注册成单例是为了让诊断/健康检查能读到它的内部游标；
        // 注意它内部的活动报警字典是无锁的，只在 ExecuteAsync 的单线程循环里变更——
        // 新增任何公开方法都必须自己保证线程安全（或改为在锁内访问）。
        services.AddSingleton<AlarmEngine>();
        return services;
    }
}
