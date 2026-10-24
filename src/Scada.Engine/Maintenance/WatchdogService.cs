using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;

namespace Scada.Engine.Maintenance;

// 故障自恢复的最后一道：关键循环停滞超过阈值即以非零退出码停止进程，由 Windows SCM 故障恢复或 systemd Restart= 拉起。
// 设备断线、历史库写失败等可恢复故障由各组件自行重试；这里只处理“卡死”。
public sealed class WatchdogService(LivenessRegistry liveness, IOptions<ScadaSettings> options, IHostApplicationLifetime lifetime, ILogger<WatchdogService> logger) : BackgroundService
{
    public const int StallExitCode = 3;
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var limit = options.Value.WatchdogStallSeconds;
        if (limit <= 0) return;
        try
        {
            using var timer = new PeriodicTimer(CheckInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var stalled = liveness.Ages().Where(x => x.Age.TotalSeconds > limit).ToList();
                if (stalled.Count == 0) continue;
                logger.LogCritical("看门狗：后台循环停滞 {Loops}，超过 {Limit} 秒，退出进程等待服务管理器重启",
                    string.Join("、", stalled.Select(x => $"{x.Name}（{x.Age.TotalSeconds:0} 秒）")), limit);
                Environment.ExitCode = StallExitCode;
                lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) { }
    }
}
