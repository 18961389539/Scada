using Microsoft.AspNetCore.SignalR;
using Scada.Server.Hubs;

namespace Scada.Server.Services;

/// <summary>
/// 画面发布后的运行端通知。实时推送失败不影响发布结果，运行端仍有周期版本检查兜底。
/// </summary>
public sealed class ScreenPublicationNotifier(IHubContext<RealtimeHub> hub)
{
    public async Task NotifyAsync(int screenId, int? publishedRevision, string? name, CancellationToken cancellationToken = default)
    {
        if (publishedRevision is not int revision) return;
        try
        {
            await hub.Clients.All.SendAsync("screenPublished", new { screenId, publishedRevision = revision, name }, cancellationToken);
        }
        catch
        {
            // 推送只是加速通道；Socket 断开或客户端很少时不能回滚已经提交的发布。
        }
    }
}
