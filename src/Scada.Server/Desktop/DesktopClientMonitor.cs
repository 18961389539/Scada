using Microsoft.Extensions.Options;
using Scada.Core.Options;

namespace Scada.Server.Desktop;

/// <summary>
/// 桌面终端离线巡检。在线状态在内存 registry 里，没人打开运维页就不会被判定；
/// 这里每 30 秒扫一遍，让"某台操作台掉线"至少落到服务端日志（通知渠道接入另行设计）。
/// </summary>
public sealed class DesktopClientMonitor(
    DesktopClientRegistry registry,
    IOptionsMonitor<ScadaSettings> settings,
    ILogger<DesktopClientMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                registry.Sweep(settings.CurrentValue.DesktopClient);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "桌面客户端离线巡检失败");
            }
        }
    }
}
