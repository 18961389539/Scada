using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Hubs;

namespace Scada.Server.Services;

public sealed class SignalRRealtimeBroadcaster : BackgroundService
{
    private readonly IRealtimeBus _bus;
    private readonly IHubContext<RealtimeHub> _hub;
    private readonly RealtimeSubscriptionStore _subscriptions;
    private readonly ScadaSettings _settings;
    private readonly ILogger<SignalRRealtimeBroadcaster> _logger;
    private readonly IConfigSnapshot _configuration;
    private readonly AccessModelProvider _access;
    private readonly SessionRegistry _sessions;

    public SignalRRealtimeBroadcaster(
        IRealtimeBus bus,
        IHubContext<RealtimeHub> hub,
        RealtimeSubscriptionStore subscriptions,
        IOptions<ScadaSettings> settings,
        ILogger<SignalRRealtimeBroadcaster> logger,
        IConfigSnapshot configuration,
        AccessModelProvider access,
        SessionRegistry sessions)
    {
        _access = access;
        _sessions = sessions;
        _bus = bus;
        _hub = hub;
        _subscriptions = subscriptions;
        _settings = settings.Value;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _bus.Subscribe();
        var buffer = new List<TagValue>();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(_settings.PushIntervalMs, 100)));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                while (reader.TryRead(out var value))
                {
                    buffer.Add(value);
                }

                if (buffer.Count == 0)
                {
                    continue;
                }

                var subscribers = _subscriptions.Snapshot();
                if (subscribers.Count > 0)
                {
                    var payloads = await BuildPayloadsAsync(subscribers, buffer, stoppingToken);
                    foreach (var (connectionId, values) in payloads)
                    {
                        try
                        {
                            await _hub.Clients.Client(connectionId).SendAsync("values", values, stoppingToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogDebug(ex, "实时数据推送失败：{ConnectionId}", connectionId);
                        }
                    }
                }

                buffer.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    // 推送前按连接所属用户的当前授权逐条过滤：订阅后被降权、设备换区域，都会立即停止推送。
    internal async Task<Dictionary<string, List<TagValue>>> BuildPayloadsAsync(
        IReadOnlyList<(string ConnectionId, IReadOnlyCollection<int> TagIds)> subscribers,
        IReadOnlyList<TagValue> buffer,
        CancellationToken cancellationToken)
    {
        var model = await _access.GetAsync(cancellationToken);
        var payloads = new Dictionary<string, List<TagValue>>();
        foreach (var (connectionId, tagIds) in subscribers)
        {
            var userId = _sessions.UserOf(connectionId);
            if (userId is null) continue;
            var access = model.For(userId);
            var selected = buffer.Where(x => tagIds.Contains(x.TagId) && access.CanTag(Permission.View, x.TagId))
                .Select(CurrentValue).OfType<TagValue>().ToList();
            if (selected.Count > 0) payloads[connectionId] = selected;
        }

        return payloads;
    }

    private TagValue? CurrentValue(TagValue value)
    {
        if (!_configuration.TryGetTag(value.TagId, out var tag)) return null;
        if (tag.Kind != Core.Enums.TagKind.Calculated) return value;
        if (value.DefinitionRevision != tag.CalculationRevision || value.Ts <= _configuration.LoadedAt) return null;
        return tag.CalculationEnabled ? value : value with { Quality = Core.Enums.TagQuality.Uncertain, Error = "计算测点已停用" };
    }
}
