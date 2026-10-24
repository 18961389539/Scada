using Microsoft.AspNetCore.SignalR;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Endpoints;
using Scada.Server.Hubs;

namespace Scada.Server.Services;

// 报警推送按区域过滤：只发给对该报警所属测点/设备有查看权限的连接。
public sealed class SignalRAlarmNotifier
    : IAlarmNotifier
{
    private readonly IHubContext<RealtimeHub> _hub;
    private readonly SessionRegistry? _sessions;
    private readonly AccessModelProvider? _access;

    public SignalRAlarmNotifier(IHubContext<RealtimeHub> hub, SessionRegistry? sessions = null, AccessModelProvider? access = null)
    {
        _hub = hub;
        _sessions = sessions;
        _access = access;
    }

    public async Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default)
    {
        if (_sessions is null || _access is null)
        {
            await _hub.Clients.All.SendAsync("alarm", new { action, alarm }, cancellationToken);
            return;
        }

        var targets = await TargetsAsync(alarm, cancellationToken);
        if (targets.Count > 0) await _hub.Clients.Clients(targets).SendAsync("alarm", new { action, alarm }, cancellationToken);
    }

    internal async Task<IReadOnlyList<string>> TargetsAsync(AlarmEvent alarm, CancellationToken cancellationToken)
    {
        var model = await _access!.GetAsync(cancellationToken);
        return _sessions!.Connections()
            .Where(x => EndpointHelpers.CanAlarm(model.For(x.UserId), Permission.View, alarm))
            .Select(x => x.ConnectionId).ToList();
    }
}
