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
    // 并发发送上限：太小仍然排队，太大会让 SignalR 写出线程与网络同时过载。
    private const int MaxConcurrentSends = 32;
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
                    // 逐个连接 await 会让慢连接拖住后面所有连接：1000 个连接即使每个 1 毫秒也要 1 秒，
                    // 正好顶满推送周期。改为受限并发，单个连接失败只影响自己。
                    await Parallel.ForEachAsync(payloads,
                        new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentSends, CancellationToken = stoppingToken },
                        async (payload, token) =>
                        {
                            try
                            {
                                await _hub.Clients.Client(payload.Key).SendAsync("values", payload.Value, token);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogDebug(ex, "实时数据推送失败：{ConnectionId}", payload.Key);
                            }
                        });
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
    // 用"测点 → 订阅连接"的反向索引组织，复杂度从 订阅者 × 变更点 降到 订阅关系数：
    // 原来的写法每个订阅者都要把整个变更集扫一遍，1000 连接 × 每周期 5000 个点就是 500 万次判断。
    internal async Task<Dictionary<string, List<TagValue>>> BuildPayloadsAsync(
        IReadOnlyList<(string ConnectionId, IReadOnlyCollection<int> TagIds)> subscribers,
        IReadOnlyList<TagValue> buffer,
        CancellationToken cancellationToken)
    {
        var model = await _access.GetAsync(cancellationToken);

        var byTag = new Dictionary<int, List<TagValue>>();
        foreach (var value in buffer)
        {
            if (CurrentValue(value) is not { } current) continue;
            if (!byTag.TryGetValue(value.TagId, out var values)) byTag[value.TagId] = values = [];
            values.Add(current);
        }
        if (byTag.Count == 0) return new();

        var watchers = new Dictionary<int, List<string>>();
        foreach (var (connectionId, tagIds) in subscribers)
        {
            foreach (var tagId in tagIds)
            {
                if (!byTag.ContainsKey(tagId)) continue;
                if (!watchers.TryGetValue(tagId, out var connections)) watchers[tagId] = connections = [];
                connections.Add(connectionId);
            }
        }
        if (watchers.Count == 0) return new();

        var payloads = new Dictionary<string, List<TagValue>>();
        foreach (var (tagId, values) in byTag)
        {
            if (!watchers.TryGetValue(tagId, out var connections)) continue;
            foreach (var connectionId in connections)
            {
                var userId = _sessions.UserOf(connectionId);
                if (userId is null) continue;
                if (!model.For(userId).CanTag(Permission.View, tagId)) continue;
                if (!payloads.TryGetValue(connectionId, out var selected)) payloads[connectionId] = selected = [];
                selected.AddRange(values);
            }
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
