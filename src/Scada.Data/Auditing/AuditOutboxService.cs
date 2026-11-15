using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scada.Data.Auditing;

/// <summary>
/// 审计发件箱的后台投递。启动时立刻跑一次（把上次进程崩溃留下的行补投），
/// 之后按固定周期清理；内联投递失败的行也就靠它补上。
/// </summary>
public sealed class AuditOutboxService(AuditOutboxDrainer drainer, ILogger<AuditOutboxService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private const int CommandTimeoutSeconds = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await TickAsync(stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken)) await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机。
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await drainer.DrainAsync(AuditOutboxDrainer.BatchSize, CommandTimeoutSeconds, cancellationToken);
        }
        catch (Exception ex)
        {
            // 单轮失败（例如运行库被长时间占用）不能终止后台循环：下一轮还要继续补投。
            logger.LogError(ex, "审计发件箱后台投递失败，等待下一轮");
        }
    }
}
