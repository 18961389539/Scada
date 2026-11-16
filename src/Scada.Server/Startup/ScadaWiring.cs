using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Health;
using Scada.Engine.Notifications;
using Scada.Server.Services;

namespace Scada.Server.Startup;

/// <summary>
/// 需要显式工厂才能构造的服务。
///
/// 与 <see cref="ScadaServerServices"/> 的分工：那边是"一行一个 <c>AddSingleton&lt;IFoo, Foo&gt;</c>"的登记表，
/// 这里只放三类无法用泛型注册表达的情况：
/// <list type="number">
///   <item>构造后还要赋值（例如 <c>AlarmNotificationDispatcher.IsActive</c> 的延迟求值委托）。</item>
///   <item>需要把另一个服务的内部实例传进去（<c>NotificationHttp.Client</c>）。</item>
///   <item>有多个同接口实现，需要按顺序组成复合体（<c>CompositeAlarmNotifier</c> 的通知链）。</item>
/// </list>
/// 把它单独放一个文件，是为了让"为什么这里要写 lambda"有答案：不看这个文件的人只会觉得注册表里混了代码。
/// </summary>
internal static class ScadaWiring
{
    /// <summary>报警外发链路：三发送器 → 分发器 → 复合通知器 → 实时推送。</summary>
    public static void AddScadaNotifications(this IServiceCollection services)
    {
        services.AddSingleton<ISecretProtector, NotificationSecrets>();
        services.AddSingleton<NotificationHttp>();
        services.AddSingleton<INotificationSender, EmailNotificationSender>();
        services.AddSingleton<INotificationSender>(sp =>
            new WebhookNotificationSender(sp.GetRequiredService<NotificationHttp>().Client));
        services.AddSingleton<INotificationSender>(sp =>
            new SmsHttpNotificationSender(sp.GetRequiredService<NotificationHttp>().Client));

        // IsActive 用委托而不是布尔值捕获：主备切换后 HaCoordinator.IsActive 会变，
        // 构造时快照会让备机永远以为自己还是主机（或反之），直接把外发发到不该发的节点上。
        services.AddSingleton(sp => new AlarmNotificationDispatcher(
            sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>(),
            sp.GetRequiredService<IDbContextFactory<RuntimeDbContext>>(),
            sp.GetRequiredService<AccessModelProvider>(),
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetServices<INotificationSender>(),
            sp.GetRequiredService<IOptions<ScadaSettings>>(),
            sp.GetRequiredService<ILogger<AlarmNotificationDispatcher>>())
        { IsActive = () => sp.GetRequiredService<INodeRole>().IsActive });
        services.AddHostedService(sp => sp.GetRequiredService<AlarmNotificationDispatcher>());

        services.AddSingleton<SignalRAlarmNotifier>();
        services.AddSingleton<IAlarmNotifier>(sp => new CompositeAlarmNotifier(
            [
                sp.GetRequiredService<SignalRAlarmNotifier>(),
                sp.GetRequiredService<AlarmWebhookNotifier>(),
                sp.GetRequiredService<AlarmNotificationDispatcher>(),
                sp.GetRequiredService<Scada.Engine.Alarms.AnnunciatorService>()
            ],
            sp.GetRequiredService<ILogger<CompositeAlarmNotifier>>()));
    }

    /// <summary>健康告警贡献者：聚合方只认接口，不再按具体类型反查。</summary>
    public static void AddScadaHealthContributors(this IServiceCollection services)
    {
        services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<Scada.Server.Ha.HaCoordinator>());
        services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<AlarmNotificationDispatcher>());
        services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<Scada.Engine.Calculations.CalcJobEngine>());
        services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<Scada.Engine.Maintenance.BackupScheduler>());
        services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<Scada.Server.Projects.ProjectService>());
        services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<Scada.Engine.Maintenance.ClockSkewMonitor>());
    }
}
