using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
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
    private readonly AlarmShelfService _shelf;
    private readonly ScadaSettings _settings;
    private readonly AlarmFloodGuard _flood;
    private readonly Dictionary<int, bool> _suppressed = new();
    private readonly Dictionary<int, Queue<long>> _raiseHistory = new();
    // 被洪水自动抑制的报警实例；洪水退出时逐个恢复，避免把条件抑制误解除。
    private readonly HashSet<int> _floodSuppressed = new();

    private readonly AlarmEvaluator _evaluator = new();
    private readonly Dictionary<int, int> _activeEventIds = new();
    private Dictionary<int, Tag> _tags = new();
    private Dictionary<int, List<AlarmRule>> _rulesByTag = new();
    private Dictionary<int, AlarmRule> _rules = new();
    private int _configVersion = -1;

    public AlarmEngine(
        IRealtimeBus bus,
        IConfigSnapshot config,
        IRealtimeCache cache,
        IDbContextFactory<ConfigDbContext> contextFactory,
        IAlarmNotifier notifier,
        ILogger<AlarmEngine> logger,
        AlarmShelfService? shelf = null,
        Microsoft.Extensions.Options.IOptions<ScadaSettings>? options = null,
        AlarmFloodGuard? floodGuard = null)
    {
        _shelf = shelf ?? new AlarmShelfService(contextFactory, notifier);
        _settings = options?.Value ?? new ScadaSettings();
        _flood = floodGuard ?? new AlarmFloodGuard(_settings);
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
        ReloadConfigIfNeeded();
        await RestoreActiveAlarmsAsync(stoppingToken);
        try { await _shelf.EnsureLoadedAsync(stoppingToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "加载报警暂挂失败"); }
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
                    await TickManagementAsync(now, stoppingToken);
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
        ReloadConfigIfNeeded();
        if (!_tags.TryGetValue(value.TagId, out var tag) || !_rulesByTag.TryGetValue(value.TagId, out var rules)) return;
        if (tag.Kind == Core.Enums.TagKind.Calculated && (value.DefinitionRevision != tag.CalculationRevision || value.Ts <= _config.LoadedAt)) return;
        var staleMs = tag.DeviceId is int deviceId && _config.TryGetDevice(deviceId, out var device) ? Math.Max(15000L, device.PollIntervalMs * 3L) : Math.Max(15000L, tag.CalculationIntervalMs * 3L);
        if (now - value.Ts > staleMs) value = value with { Quality = Core.Enums.TagQuality.Bad };
        foreach (var transition in _evaluator.Evaluate(tag, value, rules, now))
            await ApplyTransitionAsync(tag, transition with { Value = transition.Value with { Ts = now } }, cancellationToken);
    }

    private async Task ApplyTransitionAsync(Tag tag, AlarmTransition transition, CancellationToken cancellationToken)
    {
        var rule = transition.Rule;
        var areaId = AreaOf(tag);
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);

            if (transition.Kind == AlarmTransitionKind.Raise)
            {
                var decision = _flood.OnRaise(areaId, transition.Value.Ts);
                var alarm = new AlarmEvent
                {
                    TagId = tag.Id,
                    DeviceId = tag.DeviceId,
                    RuleId = rule.Id,
                    Level = rule.Level,
                    Value = transition.Value.Value,
                    StartTs = transition.Value.Ts,
                    Message = AlarmMessageBuilder.Build(tag, DeviceOf(tag), rule, transition.Value.Value, transition.PreviousValue)
                };
                ApplyFlags(alarm, rule, transition.Value.Ts);
                var floodSuppressed = ShouldSuppressByFlood(alarm, rule, decision) && !decision.Entered;
                if (floodSuppressed)
                {
                    alarm.Suppressed = true;
                    alarm.SuppressReason = FloodReason(areaId, decision.Count);
                }

                db.AlarmEvents.Add(alarm);
                await db.SaveChangesAsync(cancellationToken);
                db.AlarmActivities.Add(Activity(alarm, "raise", RaiseNote(alarm)));
                await db.SaveChangesAsync(cancellationToken);
                _activeEventIds[rule.Id] = alarm.Id;
                _suppressed[rule.Id] = alarm.Suppressed;
                if (floodSuppressed) TrackFloodSuppressed(areaId, alarm.Id);
                await NotifyAsync(alarm, "raise", cancellationToken);

                if (decision.Entered) await OnFloodEnteredAsync(areaId, decision, cancellationToken);
                if (decision.Exited) await ReleaseFloodSuppressionAsync(areaId, cancellationToken);
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

                // 洪水抑制随报警恢复结束：恢复记录仍应进入操作员视图（RTN 未确认）。
                var wasFlood = _floodSuppressed.Remove(eventId);
                if (wasFlood) _flood.RemoveSuppressed(areaId, eventId);
                alarm.EndTs = transition.Value.Ts;
                if (rule.OutOfService) alarm.EndReason = Trim($"停用：{rule.OutOfServiceReason}", 100);
                else if (!rule.Enabled) alarm.EndReason = "规则已禁用";
                if (wasFlood)
                {
                    var keep = rule.SuppressMode == AlarmSuppressMode.None ? null : SuppressionReason(rule);
                    alarm.Suppressed = keep is not null;
                    alarm.SuppressReason = keep;
                }

                _suppressed.Remove(rule.Id);
                db.AlarmActivities.Add(Activity(alarm, "recover", alarm.EndReason));
                await db.SaveChangesAsync(cancellationToken);
                await NotifyAsync(alarm, "recover", cancellationToken);
            }
            else
            {
                var decision = _flood.OnRaise(areaId, transition.Value.Ts);
                var alarm = new AlarmEvent
                {
                    TagId = tag.Id,
                    DeviceId = tag.DeviceId,
                    RuleId = rule.Id,
                    Level = rule.Level,
                    Value = transition.Value.Value,
                    StartTs = transition.Value.Ts,
                    EndTs = transition.Value.Ts,
                    Message = AlarmMessageBuilder.Build(tag, DeviceOf(tag), rule, transition.Value.Value, transition.PreviousValue)
                };
                ApplyFlags(alarm, rule, transition.Value.Ts);
                var firstOut = decision.Entered && _flood.FirstOutEnabled;
                if (firstOut) alarm.FirstOut = true;
                else if (ShouldSuppressByFlood(alarm, rule, decision))
                {
                    alarm.Suppressed = true;
                    alarm.SuppressReason = FloodReason(areaId, decision.Count);
                }

                db.AlarmEvents.Add(alarm);
                await db.SaveChangesAsync(cancellationToken);
                if (alarm.FirstOut) _flood.MarkFirstOut(areaId, alarm.Id);
                db.AlarmActivities.Add(Activity(alarm, "change", RaiseNote(alarm)));
                await db.SaveChangesAsync(cancellationToken);
                await NotifyAsync(alarm, "change", cancellationToken);

                if (decision.Entered) await OnFloodEnteredAsync(areaId, decision, cancellationToken);
                if (decision.Exited) await ReleaseFloodSuppressionAsync(areaId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "处理报警规则 {RuleId} 失败", rule.Id);
        }
    }

    private bool ShouldSuppressByFlood(AlarmEvent alarm, AlarmRule rule, AlarmFloodDecision decision) =>
        decision.Suppress && rule.Level <= _flood.SuppressMaxLevel && !alarm.Shelved && !alarm.Suppressed && !alarm.Chattering;

    private void TrackFloodSuppressed(int? areaId, int eventId)
    {
        _floodSuppressed.Add(eventId);
        _flood.TrackSuppressed(areaId, eventId);
    }

    // 进入洪水时补齐首出，并把区域内已活动的低等级报警一并抑制；只处理当前仍在报警的规则报警。
    private async Task OnFloodEnteredAsync(int? areaId, AlarmFloodDecision decision, CancellationToken cancellationToken)
    {
        if (!_flood.AutoSuppress && !_flood.FirstOutEnabled) return;
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var since = ScadaTime.NowMs() - AlarmFloodGuard.WindowMs;
            var candidates = (await db.AlarmEvents
                    .Where(x => x.EndTs == null && x.RuleId != null && x.StartTs >= since && !x.Shelved && !x.Suppressed && !x.Chattering)
                    .OrderBy(x => x.StartTs).ThenBy(x => x.Id).Take(2000).ToListAsync(cancellationToken))
                .Where(x => AreaOfTagId(x.TagId) == areaId).ToList();

            var first = _flood.FirstOutEnabled ? candidates.FirstOrDefault() : null;
            if (first is not null)
            {
                first.FirstOut = true;
                _flood.MarkFirstOut(areaId, first.Id);
                db.AlarmActivities.Add(Activity(first, "firstout", "洪水首出"));
            }

            var suppressed = _flood.AutoSuppress
                ? candidates.Where(x => x.Id != first?.Id && x.Level <= _flood.SuppressMaxLevel).ToList()
                : [];
            var reason = FloodReason(areaId, decision.Count);
            foreach (var alarm in suppressed)
            {
                alarm.Suppressed = true;
                alarm.SuppressReason = reason;
                if (alarm.RuleId is int ruleId) _suppressed[ruleId] = true;
                TrackFloodSuppressed(areaId, alarm.Id);
                db.AlarmActivities.Add(Activity(alarm, "suppress", reason));
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var alarm in suppressed) await NotifyAsync(alarm, "suppress", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "洪水抑制处理失败");
        }
    }

    private async Task ReleaseFloodSuppressionAsync(int? areaId, CancellationToken cancellationToken)
    {
        var ids = _flood.ReleaseSuppressed(areaId);
        if (ids.Count == 0) return;
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var alarms = await db.AlarmEvents.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
            var changed = new List<(AlarmEvent Alarm, string Action)>();
            foreach (var alarm in alarms)
            {
                if (!_floodSuppressed.Remove(alarm.Id)) continue;
                var rule = alarm.RuleId is int ruleId && _rules.TryGetValue(ruleId, out var found) ? found : null;
                var reason = rule is null || rule.SuppressMode == AlarmSuppressMode.None ? null : SuppressionReason(rule);
                alarm.Suppressed = reason is not null;
                alarm.SuppressReason = reason;
                if (alarm.RuleId is int id) _suppressed[id] = alarm.Suppressed;
                if (alarm.EndTs is null)
                {
                    var action = reason is null ? "unsuppress" : "suppress";
                    db.AlarmActivities.Add(Activity(alarm, action, reason ?? "洪水结束"));
                    changed.Add((alarm, action));
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var (alarm, action) in changed) await NotifyAsync(alarm, action, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "解除洪水抑制失败");
        }
    }

    private string FloodReason(int? areaId, int count)
    {
        var area = areaId is int id
            ? _config.TryGetArea(id, out var found) ? $"区域「{found.Name}」" : $"区域 #{id}"
            : "全部区域";
        var minutes = Math.Max(1, (int)(AlarmFloodGuard.WindowMs / 60000));
        return Trim($"报警洪水：{area} {minutes} 分钟内 {count} 条，自动抑制低等级报警", 200);
    }

    private Device? DeviceOf(Tag tag) =>
        tag.DeviceId is int deviceId && _config.TryGetDevice(deviceId, out var device) ? device : null;

    private int? AreaOf(Tag tag) =>
        tag.AreaId ?? (tag.DeviceId is int deviceId && _config.TryGetDevice(deviceId, out var device) ? device.AreaId : null);

    private int? AreaOfTagId(int? tagId) =>
        tagId is int id && _tags.TryGetValue(id, out var tag) ? AreaOf(tag) : null;

    private async Task RestoreActiveAlarmsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var active = await db.AlarmEvents
                .Where(x => x.EndTs == null && x.RuleId != null)
                .ToListAsync(cancellationToken);
            var dirty = false;
            foreach (var alarm in active)
            {
                // 洪水状态不跨重启保留：恢复仍带洪水抑制的活动报警，条件抑制仍有效的继续保留。
                if (alarm.Suppressed && alarm.SuppressReason?.StartsWith("报警洪水", StringComparison.Ordinal) == true)
                {
                    var keep = alarm.RuleId is int id && _rules.TryGetValue(id, out var rule) && rule.SuppressMode != AlarmSuppressMode.None
                        ? SuppressionReason(rule)
                        : null;
                    alarm.Suppressed = keep is not null;
                    alarm.SuppressReason = keep;
                    dirty = true;
                }

                if (alarm.RuleId is int ruleId)
                {
                    _evaluator.RestoreActive(ruleId);
                    _activeEventIds[ruleId] = alarm.Id;
                    _suppressed[ruleId] = alarm.Suppressed;
                }
            }

            var staleOffline = await db.AlarmEvents
                .Where(x => x.EndTs == null && x.DeviceId != null && x.RuleId == null && x.GatewayId == null
                    && !db.Devices.Any(d => d.Id == x.DeviceId && d.GatewayId != null))
                .ToListAsync(cancellationToken);
            var now = ScadaTime.NowMs();
            foreach (var alarm in staleOffline)
            {
                alarm.EndTs = now;
            }

            if (dirty || staleOffline.Count > 0)
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
        _rules = _config.AlarmRules.ToDictionary(x => x.Id);
        _configVersion = _config.Version;
    }

    private void ApplyFlags(AlarmEvent alarm, AlarmRule rule, long now)
    {
        alarm.Shelved = _shelf.IsShelved(rule.Id);
        var reason = SuppressionReason(rule);
        alarm.Suppressed = reason is not null;
        alarm.SuppressReason = reason;

        // 抖动：窗口内触发次数达到阈值即标记，标记的报警只记录不报警，提示工程上调整死区/延时。
        if (!_raiseHistory.TryGetValue(rule.Id, out var history)) _raiseHistory[rule.Id] = history = new Queue<long>();
        var window = Math.Max(_settings.AlarmChatterWindowSec, 1) * 1000L;
        while (history.Count > 0 && now - history.Peek() > window) history.Dequeue();
        history.Enqueue(now);
        alarm.Chattering = _settings.AlarmChatterCount > 0 && history.Count >= _settings.AlarmChatterCount;
    }

    // 条件抑制：条件测点质量不好时不抑制（宁可多报，不可漏报）。
    private string? SuppressionReason(AlarmRule rule)
    {
        if (rule.SuppressMode == AlarmSuppressMode.None || rule.SuppressTagId is not int conditionId) return null;
        var value = _cache.Get(conditionId);
        if (value is null || value.Quality != TagQuality.Good) return null;
        var on = value.Value >= 0.5;
        var name = _tags.TryGetValue(conditionId, out var conditionTag) ? conditionTag.Name : $"#{conditionId}";
        var reading = value.Value.ToString("0.###");
        return rule.SuppressMode switch
        {
            AlarmSuppressMode.WhenOn when on => Trim($"条件抑制：{name} = {reading}，抑制条件成立", 200),
            AlarmSuppressMode.WhenOff when !on => Trim($"条件抑制：{name} = {reading}，设备处于停机状态", 200),
            _ => null
        };
    }

    // 定时处理：暂挂到期、活动报警的抑制状态随条件变化。
    private async Task TickManagementAsync(long now, CancellationToken cancellationToken)
    {
        try
        {
            await _shelf.ExpireDueAsync(now, cancellationToken);
            foreach (var areaId in _flood.Tick(now)) await ReleaseFloodSuppressionAsync(areaId, cancellationToken);

            var rules = _rulesByTag.Values.SelectMany(x => x).Where(x => x.SuppressMode != AlarmSuppressMode.None).ToDictionary(x => x.Id);
            foreach (var (ruleId, eventId) in _activeEventIds.ToList())
            {
                if (!rules.TryGetValue(ruleId, out var rule))
                {
                    if (_floodSuppressed.Contains(eventId)) continue;
                    if (_suppressed.GetValueOrDefault(ruleId)) await SetSuppressedAsync(eventId, ruleId, null, now, cancellationToken);
                    continue;
                }
                var reason = SuppressionReason(rule);
                // 条件抑制未生效时保留洪水抑制，等待洪水退出统一释放。
                if (reason is null && _floodSuppressed.Contains(eventId)) continue;
                if ((reason is not null) != _suppressed.GetValueOrDefault(ruleId))
                    await SetSuppressedAsync(eventId, ruleId, reason, now, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "报警暂挂/抑制处理失败");
        }
    }

    private async Task SetSuppressedAsync(int eventId, int ruleId, string? reason, long now, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == eventId, cancellationToken);
        _suppressed[ruleId] = reason is not null;
        if (alarm is null || alarm.EndTs is not null) return;
        alarm.Suppressed = reason is not null;
        alarm.SuppressReason = reason;
        db.AlarmActivities.Add(Activity(alarm, reason is null ? "unsuppress" : "suppress", reason ?? "抑制条件解除"));
        await db.SaveChangesAsync(cancellationToken);
        await NotifyAsync(alarm, reason is null ? "unsuppress" : "suppress", cancellationToken);
    }

    private static AlarmActivity Activity(AlarmEvent alarm, string action, string? note) =>
        new() { AlarmEventId = alarm.Id, Ts = ScadaTime.NowMs(), Action = action, Note = note };

    private static string? RaiseNote(AlarmEvent alarm)
    {
        var flags = new List<string>();
        if (alarm.Shelved) flags.Add("已暂挂");
        if (alarm.Suppressed) flags.Add(alarm.SuppressReason ?? "已抑制");
        if (alarm.Chattering) flags.Add("抖动");
        return flags.Count == 0 ? null : string.Join("；", flags);
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

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
}
