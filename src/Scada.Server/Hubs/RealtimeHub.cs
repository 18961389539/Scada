using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Hubs;

[Authorize]
public sealed class RealtimeHub : Hub
{
    private readonly RealtimeSubscriptionStore _subscriptions;
    private readonly SessionRegistry _sessions;

    public RealtimeHub(RealtimeSubscriptionStore subscriptions, SessionRegistry sessions)
    {
        _subscriptions = subscriptions;
        _sessions = sessions;
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

    public void Subscribe(int[] tagIds) => _subscriptions.Subscribe(Context.ConnectionId, tagIds);

    public void Unsubscribe(int[] tagIds) => _subscriptions.Unsubscribe(Context.ConnectionId, tagIds);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _sessions.Unregister(Context.ConnectionId);
        _subscriptions.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
