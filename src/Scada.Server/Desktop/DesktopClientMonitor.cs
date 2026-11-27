using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Engine.Health;
using Scada.Engine.Maintenance;

namespace Scada.Server.Desktop;

/// <summary>
/// 桌面终端离线巡检与运维告警。在线状态在内存 registry 里；
/// 这里每 30 秒扫一遍，已登记且正在心跳的客户端离线时进入 /health 系统告警（运维类，不进官方工艺报警 KPI），恢复时关闭。
/// </summary>
public sealed class DesktopClientMonitor(
    DesktopClientRegistry registry,
    IOptionsMonitor<ScadaSettings> settings,
    ILogger<DesktopClientMonitor> logger) : BackgroundService, IHealthContributor
{
    public const string WarningCode = "desktop_client_offline";

    public IEnumerable<SystemWarning> HealthWarnings()
    {
        var options = settings.CurrentValue.DesktopClient;
        var offline = registry.ListCurrentlyOffline(options);
        foreach (var client in offline)
        {
            var lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(client.LastSeenTs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var target = string.IsNullOrWhiteSpace(client.TargetName) ? client.TargetKind : client.TargetName;
            var suffix = string.IsNullOrWhiteSpace(target) ? "" : $"，目标 {target}";
            yield return new SystemWarning(WarningCode,
                $"桌面客户端 {client.MachineName}（{client.ClientId}）已离线，最后心跳 {lastSeen}{suffix}；请核对操作台或值班工位是否在线");
        }
    }

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
