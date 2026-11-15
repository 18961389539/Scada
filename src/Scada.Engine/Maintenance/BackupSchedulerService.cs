using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scada.Engine.Maintenance;

// 定时备份的驱动节拍：每分钟探一次，"该不该真跑"由 BackupScheduler 按 FullBackupIntervalHours 判断，
// 并发由它自己的 SemaphoreSlim 挡。原先用 Quartz 的 SimpleTrigger 做同一件事，但调度器只服务这一个 job，
// 却要额外承担 misfire 语义、作业存储与停机中断策略；PeriodicTimer 足够，停机时也不会挂住进程。
public sealed class BackupSchedulerService(
    BackupScheduler backups,
    ILogger<BackupSchedulerService> logger,
    TimeSpan? interval = null) : BackgroundService
{
    private readonly TimeSpan _interval = interval is { } value && value > TimeSpan.Zero
        ? value
        : TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var run = await backups.RunScheduledAsync(stoppingToken);
                    if (run is not null)
                        logger.LogInformation("定时备份完成：{File}，演练 {Drill}，异地 {Targets}",
                            run.FileName, run.Drill?.Ok, string.Join(",", run.Targets.Select(target => $"{target.Target}:{target.Ok}")));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "定时备份流水线异常");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }
}
