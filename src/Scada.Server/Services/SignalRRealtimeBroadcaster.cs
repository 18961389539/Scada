using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
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

    public SignalRRealtimeBroadcaster(
        IRealtimeBus bus,
        IHubContext<RealtimeHub> hub,
        RealtimeSubscriptionStore subscriptions,
        IOptions<ScadaSettings> settings,
        ILogger<SignalRRealtimeBroadcaster> logger,
        IConfigSnapshot configuration)
    {
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
                    var payloads = new Dictionary<string, List<TagValue>>();
                    foreach (var (connectionId, tagIds) in subscribers)
                    {
                        var selected = buffer.Where(x => tagIds.Contains(x.TagId)).Select(CurrentValue).OfType<TagValue>().ToList();
                        if (selected.Count > 0)
                        {
                            payloads[connectionId] = selected;
                        }
                    }

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

    private TagValue? CurrentValue(TagValue value)
    {
        if (!_configuration.TryGetTag(value.TagId, out var tag)) return null;
        if (tag.Kind != Core.Enums.TagKind.Calculated) return value;
        if (value.DefinitionRevision != tag.CalculationRevision || value.Ts <= _configuration.LoadedAt) return null;
        return tag.CalculationEnabled ? value : value with { Quality = Core.Enums.TagQuality.Uncertain, Error = "计算测点已停用" };
    }
}
