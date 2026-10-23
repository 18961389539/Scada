using Microsoft.AspNetCore.SignalR;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Server.Hubs;

namespace Scada.Server.Services;

public sealed class SignalRAlarmNotifier : IAlarmNotifier
{
    private readonly IHubContext<RealtimeHub> _hub;

    public SignalRAlarmNotifier(IHubContext<RealtimeHub> hub)
    {
        _hub = hub;
    }

    public Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default) =>
        _hub.Clients.All.SendAsync("alarm", new { action, alarm }, cancellationToken);
}
