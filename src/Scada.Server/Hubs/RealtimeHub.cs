using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Scada.Server.Services;

namespace Scada.Server.Hubs;

[Authorize]
public sealed class RealtimeHub : Hub
{
    private readonly RealtimeSubscriptionStore _subscriptions;

    public RealtimeHub(RealtimeSubscriptionStore subscriptions)
    {
        _subscriptions = subscriptions;
    }

    public void Subscribe(int[] tagIds) => _subscriptions.Subscribe(Context.ConnectionId, tagIds);

    public void Unsubscribe(int[] tagIds) => _subscriptions.Unsubscribe(Context.ConnectionId, tagIds);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _subscriptions.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
