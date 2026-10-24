using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;

namespace Scada.Engine.Notifications;

public sealed record NotificationDelivery(int RouteId, int ChannelId, string Action, bool Success, string? Error)
{
    public int AlarmEventId { get; init; }
    public int Attempt { get; init; }
    public bool Final { get; init; }
}

// 报警通知分发：按优先级（等级）、区域、班次把报警路由到邮件/webhook/短信通道；未确认超时按升级路由再发。
// 入口只入队不等待网络；每次投递写 AlarmNotificationLog 并在报警时间线留痕。
public sealed class AlarmNotificationDispatcher : BackgroundService, IAlarmNotifier
{
    private readonly IDbContextFactory<ConfigDbContext> _factory;
    private readonly AccessModelProvider _access;
    private readonly ISecretProtector _secrets;
    private readonly IReadOnlyDictionary<NotificationChannelType, INotificationSender> _senders;
    private readonly ScadaSettings _settings;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Channel<(AlarmEvent Alarm, string Action)> _queue =
        Channel.CreateBounded<(AlarmEvent, string)>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private long _sent, _failed, _dropped;
    private string? _lastError;

    public AlarmNotificationDispatcher(
        IDbContextFactory<ConfigDbContext> factory,
        AccessModelProvider access,
        ISecretProtector secrets,
        IEnumerable<INotificationSender> senders,
        IOptions<ScadaSettings> options,
        ILogger<AlarmNotificationDispatcher> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _factory = factory;
        _access = access;
        _secrets = secrets;
        _senders = senders.GroupBy(x => x.Type).ToDictionary(g => g.Key, g => g.Last());
        _settings = options.Value;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    // 抢占期限：须大于任何通道的发送超时上限（见 NotificationChannelSettings.MaxTimeoutMs）。
    public const long LeaseMs = 5 * 60_000L;
    public long Sent => Interlocked.Read(ref _sent);
    public long Failed => Interlocked.Read(ref _failed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public string? LastError => _lastError;
    // 最近一次投递后的发件箱快照：待发（含重试中）与最终失败条数，供 /health 同步读取。
    public int Pending => Volatile.Read(ref _pending);
    public int FinalFailed => Volatile.Read(ref _finalFailed);
    private int _pending, _finalFailed;

    public IReadOnlyList<string> Warnings() => FinalFailed > 0
        ? [$"最近 24 小时有 {FinalFailed} 条报警通知最终发送失败（未知悉），详见报警通知页、报警时间线或诊断包 notifications.json"]
        : [];

    public Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default)
    {
        if (action is not ("raise" or "recover" or "unshelve" or "unsuppress")) return Task.CompletedTask;
        var copy = Clone(alarm);
        if (!_queue.Writer.TryWrite((copy, action))) Interlocked.Increment(ref _dropped);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scan = Task.Run(() => EscalationLoopAsync(stoppingToken), stoppingToken);
        try
        {
            await foreach (var (alarm, action) in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try { await ProcessAsync(alarm, action, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "报警通知分发失败"); }
            }
        }
        catch (OperationCanceledException) { }
        try { await scan; } catch (OperationCanceledException) { }
    }

    private async Task EscalationLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(_settings.AlarmEscalationScanSec, 1)));
        var nextPurge = 0L;
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (Environment.TickCount64 >= nextPurge)
            {
                nextPurge = Environment.TickCount64 + 3_600_000;
                try { await PurgeAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "发件箱清理失败"); }
            }
            try { await ScanEscalationsAsync(ct); } // 内含 DrainAsync：顺带重试到期的失败通知
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "报警升级扫描失败"); }
        }
    }

    // 处理一条报警事件：匹配路由写入发件箱（幂等），随即投递到期条目；返回本报警的投递结果（测试与诊断用）。
    public async Task<IReadOnlyList<NotificationDelivery>> ProcessAsync(AlarmEvent alarm, string action, CancellationToken ct = default)
    {
        if (action is "unshelve" or "unsuppress")
        {
            // 暂挂/抑制解除后仍活动且未确认，按新触发补发（已入发件箱的路由不重复）。
            if (alarm.EndTs is not null || alarm.AckTs is not null) return [];
            action = "raise";
        }
        if (!AlarmStates.Annunciated(alarm)) return [];

        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var routes = await MatchingRoutesAsync(db, alarm, escalation: false, ct);
            foreach (var (route, channel) in routes.Where(r => action == "raise" ? r.Route.OnRaise : r.Route.OnRecover))
                await EnqueueAsync(alarm.Id, route.Id, channel.Id, action, ct);
        }
        return (await DrainAsync(ct)).Where(x => x.AlarmEventId == alarm.Id).ToList();
    }

    public async Task<IReadOnlyList<NotificationDelivery>> ScanEscalationsAsync(CancellationToken ct = default)
    {
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var escalations = await db.NotificationRoutes.AsNoTracking().Where(x => x.Enabled && x.EscalateAfterMinutes != null).ToListAsync(ct);
            if (escalations.Count > 0)
            {
                var now = _clock().ToUnixTimeMilliseconds();
                var minMs = escalations.Min(x => Math.Max(x.EscalateAfterMinutes!.Value, 0)) * 60_000L;
                var candidates = await db.AlarmEvents.AsNoTracking()
                    .Where(x => x.EndTs == null && x.AckTs == null && !x.Shelved && !x.Suppressed && !x.Chattering && x.StartTs <= now - minMs)
                    .OrderBy(x => x.StartTs).Take(500).ToListAsync(ct);
                foreach (var alarm in candidates)
                    foreach (var (route, channel) in await MatchingRoutesAsync(db, alarm, escalation: true, ct))
                        if (now - alarm.StartTs >= route.EscalateAfterMinutes!.Value * 60_000L)
                            await EnqueueAsync(alarm.Id, route.Id, channel.Id, "escalate", ct);
            }
        }
        return await DrainAsync(ct);
    }

    // 幂等入队：DedupKey 唯一约束保证同一报警×路由×动作只有一行，扫描与手动/事件触发并发时也只发一次。
    private async Task<bool> EnqueueAsync(int alarmId, int routeId, int channelId, string action, CancellationToken ct)
    {
        if (alarmId <= 0) return false;
        var key = $"{action}:{alarmId}:{routeId}";
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (await db.NotificationOutbox.AnyAsync(x => x.DedupKey == key, ct)) return false;
        var now = _clock().ToUnixTimeMilliseconds();
        db.NotificationOutbox.Add(new NotificationOutbox
        {
            DedupKey = key, AlarmEventId = alarmId, RouteId = routeId, ChannelId = channelId, Action = action,
            MaxAttempts = Math.Max(_settings.NotificationMaxAttempts, 1), NextAttemptTs = now, CreatedTs = now
        });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { return false; } // 并发入队撞上唯一约束：已有人入队
    }

    // 投递所有到期条目。每条先以条件更新“认领”（Pending→Sending 并占租约），认领成功者才发送，避免多处并发重复发送。
    // 租约过期的 Sending（进程在发送中崩溃）会被重新认领；这类情况下接收端可能收到一次重复，属于至少一次语义。
    public async Task<IReadOnlyList<NotificationDelivery>> DrainAsync(CancellationToken ct = default)
    {
        var results = new List<NotificationDelivery>();
        await _drainGate.WaitAsync(ct);
        try
        {
            var now = _clock().ToUnixTimeMilliseconds();
            List<long> due;
            await using (var db = await _factory.CreateDbContextAsync(ct))
            {
                due = await db.NotificationOutbox.AsNoTracking()
                    .Where(x => (x.Status == NotificationOutboxStatus.Pending && x.NextAttemptTs <= now)
                        || (x.Status == NotificationOutboxStatus.Sending && x.LeaseUntilTs < now))
                    .OrderBy(x => x.NextAttemptTs).Select(x => x.Id).Take(200).ToListAsync(ct);
            }
            foreach (var id in due)
                if (await DeliverOutboxAsync(id, now, ct) is { } delivery) results.Add(delivery);
        }
        finally { _drainGate.Release(); }
        try
        {
            var stats = await OutboxStatsAsync(ct);
            _pending = stats.Pending; _finalFailed = stats.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "统计发件箱失败"); }
        return results;
    }

    // Failed 只统计最近 24 小时内最终失败且未“已知悉”的条目。
    public async Task<(int Pending, int Failed, int Sent)> OutboxStatsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var since = _clock().ToUnixTimeMilliseconds() - 24 * 3_600_000L;
        var groups = await db.NotificationOutbox.AsNoTracking().Where(x => x.Status != NotificationOutboxStatus.Failed).GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(string s) => groups.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        var failed = await db.NotificationOutbox.CountAsync(x => x.Status == NotificationOutboxStatus.Failed && x.AcknowledgedTs == null && x.FinishedTs >= since, ct);
        return (Count(NotificationOutboxStatus.Pending) + Count(NotificationOutboxStatus.Sending), failed, Count(NotificationOutboxStatus.Sent));
    }

    public async Task RefreshStatsAsync(CancellationToken ct = default)
    {
        var stats = await OutboxStatsAsync(ct);
        _pending = stats.Pending; _finalFailed = stats.Failed;
    }

    // 保留期清理：删除早于保留天数的已发送/最终失败记录；待发与发送中的记录永不清理。
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var cutoff = _clock().ToUnixTimeMilliseconds() - Math.Max(1, _settings.NotificationOutboxRetentionDays) * 86_400_000L;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var removed = await db.NotificationOutbox
            .Where(x => (x.Status == NotificationOutboxStatus.Sent || x.Status == NotificationOutboxStatus.Failed) && (x.FinishedTs ?? x.CreatedTs) < cutoff)
            .ExecuteDeleteAsync(ct);
        if (removed > 0) _logger.LogInformation("已清理 {Count} 条过期发件箱记录", removed);
        return removed;
    }

    private async Task<NotificationDelivery?> DeliverOutboxAsync(long id, long now, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var lease = now + LeaseMs;
        var claimed = await db.NotificationOutbox
            .Where(x => x.Id == id && ((x.Status == NotificationOutboxStatus.Pending && x.NextAttemptTs <= now) || (x.Status == NotificationOutboxStatus.Sending && x.LeaseUntilTs < now)))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, NotificationOutboxStatus.Sending).SetProperty(x => x.LeaseUntilTs, lease).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
        if (claimed != 1) return null;

        var item = await db.NotificationOutbox.SingleAsync(x => x.Id == id, ct);
        var alarm = await db.AlarmEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.AlarmEventId, ct);
        var route = await db.NotificationRoutes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.RouteId, ct);
        var channel = await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == item.ChannelId, ct);
        string? error = null;
        if (alarm is null || route is null || channel is null) error = "报警、路由或通道已删除";
        else if (!channel.Enabled || !route.Enabled) error = "路由或通道已停用";
        else if (item.Action == "escalate" && (alarm.AckTs is not null || alarm.EndTs is not null)) error = null; // 已确认/恢复：升级不再需要，按完成处理
        else error = await SendAsync(db, alarm, route, channel, item.Action, ct);

        var skipped = alarm is not null && item.Action == "escalate" && (alarm.AckTs is not null || alarm.EndTs is not null);
        var finalNow = _clock().ToUnixTimeMilliseconds();
        var permanent = alarm is null || route is null || channel is null || (channel is not null && route is not null && (!channel.Enabled || !route.Enabled));
        item.LeaseUntilTs = null;
        item.LastError = error;
        if (error is null)
        {
            item.Status = NotificationOutboxStatus.Sent;
            item.SentTs = finalNow; item.FinishedTs = finalNow;
        }
        else if (permanent || item.Attempts >= item.MaxAttempts)
        {
            item.Status = NotificationOutboxStatus.Failed; item.FinishedTs = finalNow;
        }
        else
        {
            item.Status = NotificationOutboxStatus.Pending;
            var backoff = Math.Min(Math.Max(_settings.NotificationRetryBaseSec, 0) * Math.Pow(2, item.Attempts - 1), Math.Max(_settings.NotificationRetryMaxSec, 0));
            item.NextAttemptTs = finalNow + (long)(backoff * 1000);
        }

        if (alarm is not null)
        {
            var label = item.Action == "escalate" ? "升级通知" : "通知";
            var name = route?.Name ?? $"#{item.RouteId}";
            var via = channel?.Name ?? $"#{item.ChannelId}";
            if (!skipped)
                db.AlarmNotificationLogs.Add(new AlarmNotificationLog { AlarmEventId = alarm.Id, RouteId = item.RouteId, ChannelId = item.ChannelId, Action = item.Action, Ts = finalNow, Success = error is null, Error = error });
            string? note = skipped ? null
                : error is null ? $"{label}「{name}」经 {via}：已发送（第 {item.Attempts} 次）"
                : item.Status == NotificationOutboxStatus.Failed ? $"{label}「{name}」经 {via}：最终失败（共 {item.Attempts} 次）{error}"
                : $"{label}「{name}」经 {via}：第 {item.Attempts} 次失败，{TimeSpan.FromMilliseconds(item.NextAttemptTs - finalNow).TotalSeconds:0} 秒后重试：{error}";
            if (note is not null)
                db.AlarmActivities.Add(new AlarmActivity
                {
                    AlarmEventId = alarm.Id, Ts = finalNow,
                    Action = item.Status == NotificationOutboxStatus.Failed ? "notify-failed" : item.Action == "escalate" ? "escalate" : "notify",
                    Note = note
                });
        }
        await db.SaveChangesAsync(ct);
        return new(item.RouteId, item.ChannelId, item.Action, error is null, error) { AlarmEventId = item.AlarmEventId, Attempt = item.Attempts, Final = item.Status != NotificationOutboxStatus.Pending };
    }

    public async Task SendTestAsync(NotificationChannel channel, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        if (!_senders.TryGetValue(channel.Type, out var sender)) throw new InvalidOperationException("不支持的通道类型");
        var alarm = new AlarmEvent { Id = 0, Level = AlarmLevel.Info, Message = "SCADA 通知通道测试", StartTs = _clock().ToUnixTimeMilliseconds() };
        await sender.SendAsync(NotificationChannelSettings.Parse(channel.SettingsJson), Secret(channel), recipients, Compose(alarm, "test", "通道测试", []), ct);
    }

    private async Task<List<(NotificationRoute Route, NotificationChannel Channel)>> MatchingRoutesAsync(ConfigDbContext db, AlarmEvent alarm, bool escalation, CancellationToken ct)
    {
        var routes = await db.NotificationRoutes.AsNoTracking().Where(x => x.Enabled && (x.EscalateAfterMinutes != null) == escalation).ToListAsync(ct);
        if (routes.Count == 0) return [];
        var channels = await db.NotificationChannels.AsNoTracking().Where(x => x.Enabled).ToDictionaryAsync(x => x.Id, ct);
        var shifts = await db.DutyShifts.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var areas = await AreasOfAsync(alarm, ct);
        var local = ToShiftTime(_clock());
        return routes
            .Where(r => alarm.Level >= r.MinLevel)
            .Where(r => r.AreaId is not int area || areas.Contains(area))
            .Where(r => r.ShiftId is not int s || (shifts.TryGetValue(s, out var shift) && ShiftActive(shift, local)))
            .Where(r => channels.ContainsKey(r.ChannelId))
            .Select(r => (r, channels[r.ChannelId]))
            .ToList();
    }

    // 报警所属区域：测点按授权模型推导（计算测点可能跨多个区域），设备按设备区域；网关级报警不属于任何区域，只走全区域路由。
    public async Task<IReadOnlyList<int>> AreasOfAsync(AlarmEvent alarm, CancellationToken ct)
    {
        var model = await _access.GetAsync(ct);
        if (alarm.TagId is int tag) return model.AreasOfTag(tag);
        if (alarm.DeviceId is int device) return model.DeviceAreas.TryGetValue(device, out var area) ? [area] : [Area.DefaultId];
        return [];
    }

    private async Task<string?> SendAsync(ConfigDbContext db, AlarmEvent alarm, NotificationRoute route, NotificationChannel channel, string action, CancellationToken ct)
    {
        try
        {
            if (!_senders.TryGetValue(channel.Type, out var sender)) throw new InvalidOperationException("不支持的通道类型");
            var areaNames = await db.Areas.AsNoTracking().Select(a => new { a.Id, a.Name }).ToListAsync(ct);
            var ids = await AreasOfAsync(alarm, ct);
            var names = areaNames.Where(a => ids.Contains(a.Id)).Select(a => a.Name).ToList();
            var settings = NotificationChannelSettings.Parse(channel.SettingsJson);
            // 强制超时：即使通道实现未遵守超时，也在抢占期限前中止。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(settings.EffectiveTimeoutMs(channel.Type) + 1000);
            try { await sender.SendAsync(settings, Secret(channel), SplitRecipients(route.Recipients), Compose(alarm, action, route.Name, names), timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"发送超时（{settings.EffectiveTimeoutMs(channel.Type)} ms）"); }
            Interlocked.Increment(ref _sent);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var error = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            _lastError = error;
            Interlocked.Increment(ref _failed);
            _logger.LogWarning("报警通知 {Route} 经通道 {Channel} 发送失败：{Error}", route.Name, channel.Name, error);
            return error;
        }
    }

    private string? Secret(NotificationChannel channel)
    {
        if (string.IsNullOrEmpty(channel.SecretProtected)) return null;
        try { return _secrets.Unprotect(channel.SecretProtected); }
        catch (Exception) { throw new InvalidOperationException("通道凭据无法解密，请重新填写"); }
    }

    private NotificationMessage Compose(AlarmEvent alarm, string action, string routeName, IReadOnlyList<string> areaNames)
    {
        var level = alarm.Level switch { AlarmLevel.Critical => "严重", AlarmLevel.Warning => "警告", _ => "提示" };
        var verb = action switch { "raise" => "报警", "recover" => "恢复", "escalate" => "未确认升级", "test" => "测试", _ => action };
        var time = ToShiftTime(DateTimeOffset.FromUnixTimeMilliseconds(alarm.StartTs)).ToString("yyyy-MM-dd HH:mm:ss");
        var area = areaNames.Count == 0 ? "-" : string.Join("、", areaNames);
        var subject = $"[SCADA][{level}][{verb}] {alarm.Message}";
        var body = $"{verb}：{alarm.Message}\n等级：{level}\n数值：{alarm.Value:0.###}\n开始：{time}\n区域：{area}\n报警号：#{alarm.Id}";
        var payload = new { type = "alarm", action, route = routeName, areas = areaNames, sentAt = DateTimeOffset.UtcNow, alarm };
        return new(subject, body, payload);
    }

    private DateTimeOffset ToShiftTime(DateTimeOffset utc)
    {
        var zone = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(_settings.ShiftTimeZone))
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(_settings.ShiftTimeZone); }
            catch (Exception) { /* 时区无效时回退本机时区 */ }
        }
        return TimeZoneInfo.ConvertTime(utc, zone);
    }

    public static bool ShiftActive(DutyShift shift, DateTimeOffset local)
    {
        if (!shift.Enabled) return false;
        var minute = local.Hour * 60 + local.Minute;
        bool Day(DayOfWeek d) => (shift.Days & (1 << (int)d)) != 0;
        var today = local.DayOfWeek;
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);
        if (shift.StartMinute == shift.EndMinute) return Day(today);
        if (shift.StartMinute < shift.EndMinute) return Day(today) && minute >= shift.StartMinute && minute < shift.EndMinute;
        return (Day(today) && minute >= shift.StartMinute) || (Day(yesterday) && minute < shift.EndMinute);
    }

    public static IReadOnlyList<string> SplitRecipients(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r', '，', '；', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();

    private static AlarmEvent Clone(AlarmEvent a) => new()
    {
        Id = a.Id, TagId = a.TagId, DeviceId = a.DeviceId, GatewayId = a.GatewayId, EndReason = a.EndReason, RuleId = a.RuleId, Level = a.Level,
        Value = a.Value, StartTs = a.StartTs, EndTs = a.EndTs, AckUser = a.AckUser, AckTs = a.AckTs, Message = a.Message,
        Shelved = a.Shelved, Suppressed = a.Suppressed, SuppressReason = a.SuppressReason, Chattering = a.Chattering
    };
}
