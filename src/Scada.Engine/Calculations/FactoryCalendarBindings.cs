using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Configuration;
using Scada.Core.Interfaces;
using Scada.Data.Infrastructure;

namespace Scada.Engine.Calculations;

/// <summary>
/// 工厂日历变更后：重跑历史小时网格对齐，并让计算任务按新时区重载周期。
/// </summary>
public sealed class FactoryCalendarBindings : IHostedService
{
    public FactoryCalendarBindings(
        IFactoryCalendar calendar,
        IHistoryDbFactory history,
        CalcJobEngine? jobs = null,
        IConfigChangeNotifier? changes = null,
        ILogger<FactoryCalendarBindings>? logger = null)
    {
        var log = logger ?? NullLogger<FactoryCalendarBindings>.Instance;
        calendar.Changed += () =>
        {
            try { history.RealignExisting(); }
            catch (Exception ex) { log.LogWarning(ex, "时区变更后对齐历史小时网格失败"); }
            try { jobs?.RequestReload(); }
            catch (Exception ex) { log.LogWarning(ex, "时区变更后重载计算任务失败"); }
        };
        changes?.Subscribe(set =>
        {
            if (set.Jobs) jobs?.RequestReload();
        });
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
