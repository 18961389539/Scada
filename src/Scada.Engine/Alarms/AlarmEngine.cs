using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Alarms;

public sealed class AlarmEngine : BackgroundService
{
    private readonly IRealtimeBus _bus;
    private readonly IConfigSnapshot _config;
    private readonly IRealtimeCache _cache;
    private readonly IDbContextFactory<ConfigDbContext> _contextFactory;
    private readonly IAlarmNotifier _notifier;
    private readonly ILogger<AlarmEngine> _logger;

    private readonly AlarmEvaluator _evaluator = new();
    private readonly Dictionary<int, int> _activeEventIds = new();
    private Dictionary<int, Tag> _tags = new();
    private Dictionary<int, List<AlarmRule>> _rulesByTag = new();
    private int _configVersion = -1;

    public AlarmEngine(
        IRealtimeBus bus,
        IConfigSnapshot config,
        IRealtimeCache cache,
        IDbContextFactory<ConfigDbContext> contextFactory,
        IAlarmNotifier notifier,
        ILogger<AlarmEngine> logger)
    {
        _bus = bus;
        _config = config;
        _cache = cache;
        _contextFactory = contextFactory;
        _notifier = notifier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _bus.Subscribe();
        await RestoreActiveAlarmsAsync(stoppingToken);
        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var tick = ticker.WaitForNextTickAsync(stoppingToken).AsTask();
        var ready = reader.WaitToReadAsync(stoppingToken).AsTask();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(ready, tick);
                ReloadConfigIfNeeded();
                if (completed == ready)
                {
                    while (reader.TryRead(out var value)) await EvaluateAsync(value, value.Ts, stoppingToken);
                    ready = reader.WaitToReadAsync(stoppingToken).AsTask();
                }
                if (tick.IsCompleted)
                {
                    if (!await tick) break;
                    var now = ScadaTime.NowMs();
                    foreach (var tagId in _rulesByTag.Keys)
                    {
                        var value = _cache.Get(tagId);
                        if (value is not null) await EvaluateAsync(value, now, stoppingToken);
                    }
                    tick = ticker.WaitForNextTickAsync(stoppingToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task EvaluateAsync(TagValue value, long now, CancellationToken cancellationToken)
    {
        if (!_tags.TryGetValue(value.TagId, out var tag) || !_rulesByTag.TryGetValue(value.TagId, out var rules)) return;
        var staleMs = _config.TryGetDevice(tag.DeviceId, out var device) ? Math.Max(15000L, device.PollIntervalMs * 3L) : 15000L;
        if (now - value.Ts > staleMs) value = value with { Quality = Core.Enums.TagQuality.Bad };
        foreach (var transition in _evaluator.Evaluate(tag, value, rules, now))
            await ApplyTransitionAsync(tag, transition with { Value = transition.Value with { Ts = now } }, cancellationToken);
    }

    private async Task ApplyTransitionAsync(Tag tag, AlarmTransition transition, CancellationToken cancellationToken)
    {
        var rule = transition.Rule;
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

            if (transition.Kind == AlarmTransitionKind.Raise)
            {
                var alarm = new AlarmEvent
                {
                    TagId = tag.Id,
                    DeviceId = tag.DeviceId,
                    RuleId = rule.Id,
                    Level = rule.Level,
                    Value = transition.Value.Value,
                    StartTs = transition.Value.Ts,
                    Message = BuildMessage(tag, rule)
                };
                db.AlarmEvents.Add(alarm);
                await db.SaveChangesAsync(cancellationToken);
                _activeEventIds[rule.Id] = alarm.Id;
                await NotifyAsync(alarm, "raise", cancellationToken);
            }
            else if (transition.Kind == AlarmTransitionKind.Recover)
            {
                if (!_activeEventIds.Remove(rule.Id, out var eventId))
                {
                    return;
                }

                var alarm = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == eventId, cancellationToken);
                if (alarm is null || alarm.EndTs is not null)
                {
                    return;
                }

                alarm.EndTs = transition.Value.Ts;
                await db.SaveChangesAsync(cancellationToken);
                await NotifyAsync(alarm, "recover", cancellationToken);
            }
            else
            {
                var alarm = new AlarmEvent
                {
                    TagId = tag.Id,
                    DeviceId = tag.DeviceId,
                    RuleId = rule.Id,
                    Level = rule.Level,
                    Value = transition.Value.Value,
                    StartTs = transition.Value.Ts,
                    EndTs = transition.Value.Ts,
                    Message = BuildMessage(tag, rule)
                };
                db.AlarmEvents.Add(alarm);
                await db.SaveChangesAsync(cancellationToken);
                await NotifyAsync(alarm, "change", cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "处理报警规则 {RuleId} 失败", rule.Id);
        }
    }

    private async Task RestoreActiveAlarmsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var active = await db.AlarmEvents
                .Where(x => x.EndTs == null && x.RuleId != null)
                .ToListAsync(cancellationToken);
            foreach (var alarm in active)
            {
                if (alarm.RuleId is int ruleId)
                {
                    _evaluator.RestoreActive(ruleId);
                    _activeEventIds[ruleId] = alarm.Id;
                }
            }

            var staleOffline = await db.AlarmEvents
                .Where(x => x.EndTs == null && x.DeviceId != null && x.RuleId == null)
                .ToListAsync(cancellationToken);
            var now = ScadaTime.NowMs();
            foreach (var alarm in staleOffline)
            {
                alarm.EndTs = now;
            }

            if (staleOffline.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "恢复报警状态失败");
        }
    }

    private void ReloadConfigIfNeeded()
    {
        if (_config.Version == _configVersion)
        {
            return;
        }

        _tags = _config.Tags.ToDictionary(x => x.Id);
        _rulesByTag = _config.AlarmRules
            .GroupBy(x => x.TagId)
            .ToDictionary(x => x.Key, x => x.ToList());
        _configVersion = _config.Version;
    }

    private async Task NotifyAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.PublishAlarmAsync(alarm, action, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "报警推送失败");
        }
    }

    private static string BuildMessage(Tag tag, AlarmRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Message))
        {
            return rule.Message;
        }

        var label = rule.Type switch
        {
            AlarmRuleType.HighHigh => "高高限",
            AlarmRuleType.High => "高限",
            AlarmRuleType.Low => "低限",
            AlarmRuleType.LowLow => "低低限",
            AlarmRuleType.StateChange => "状态变化",
            _ => "报警"
        };

        return rule.Type == AlarmRuleType.StateChange
            ? $"{tag.Name} {label}"
            : $"{tag.Name} {label}（阈值 {rule.Threshold:0.###}）";
    }
}
