using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Hubs;

[Authorize]
public sealed class RealtimeHub : Hub
{
    private readonly RealtimeSubscriptionStore _subscriptions;
    private readonly SessionRegistry _sessions;
    private readonly AccessModelProvider _access;

    public RealtimeHub(RealtimeSubscriptionStore subscriptions, SessionRegistry sessions, AccessModelProvider access)
    {
        _subscriptions = subscriptions;
        _sessions = sessions;
        _access = access;
    }

    public override Task OnConnectedAsync()
    {
        // 长连接的身份在建立时被固定，这里登记快照，供管理操作或周期检查主动断开。
        if (Context.User?.GetUserId() is int userId)
        {
            _sessions.Register(Context.ConnectionId, userId,
                Context.User.FindFirstValue(ClaimTypes.Role),
                Context.User.FindFirstValue("credentialsVersion"),
                Context.Abort);
        }

        return base.OnConnectedAsync();
    }

    // 订阅时即过滤无权测点，返回实际生效的测点；推送时还会按当前授权再过滤一次。
    public async Task<int[]> Subscribe(int[] tagIds)
    {
        var access = await _access.ForUserAsync(Context.User?.GetUserId());
        var allowed = (tagIds ?? []).Where(id => access.CanTag(Permission.View, id)).Distinct().ToArray();
        _subscriptions.Subscribe(Context.ConnectionId, allowed);
        return allowed;
    }

    public void Unsubscribe(int[] tagIds) => _subscriptions.Unsubscribe(Context.ConnectionId, tagIds);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _sessions.Unregister(Context.ConnectionId);
        _subscriptions.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
