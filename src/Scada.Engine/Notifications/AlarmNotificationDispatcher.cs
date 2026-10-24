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

public sealed record NotificationDelivery(int RouteId, int ChannelId, string Action, bool Success, string? Error);

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

    public long Sent => Interlocked.Read(ref _sent);
    public long Failed => Interlocked.Read(ref _failed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public string? LastError => _lastError;

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
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await ScanEscalationsAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "报警升级扫描失败"); }
        }
    }

    // 处理一条报警事件；返回实际投递结果（测试与诊断用）。
    public async Task<IReadOnlyList<NotificationDelivery>> ProcessAsync(AlarmEvent alarm, string action, CancellationToken ct = default)
    {
        if (action is "unshelve" or "unsuppress")
        {
            // 暂挂/抑制解除后仍活动且未确认，按新触发补发（已发过的路由不重复）。
            if (alarm.EndTs is not null || alarm.AckTs is not null) return [];
            action = "raise";
        }
        if (!AlarmStates.Annunciated(alarm)) return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        var routes = await MatchingRoutesAsync(db, alarm, escalation: false, ct);
        routes = routes.Where(r => action == "raise" ? r.Route.OnRaise : r.Route.OnRecover).ToList();
        var results = new List<NotificationDelivery>();
        foreach (var (route, channel) in routes)
        {
            if (action == "raise" && await db.AlarmNotificationLogs.AnyAsync(x => x.AlarmEventId == alarm.Id && x.RouteId == route.Id && x.Action == "raise" && x.Success, ct))
                continue;
            results.Add(await DeliverAsync(db, alarm, route, channel, action, ct));
        }
        return results;
    }

    public async Task<IReadOnlyList<NotificationDelivery>> ScanEscalationsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var escalations = await db.NotificationRoutes.AsNoTracking().Where(x => x.Enabled && x.EscalateAfterMinutes != null).ToListAsync(ct);
        if (escalations.Count == 0) return [];
        var now = _clock().ToUnixTimeMilliseconds();
        var minMs = escalations.Min(x => Math.Max(x.EscalateAfterMinutes!.Value, 0)) * 60_000L;
        var candidates = await db.AlarmEvents.AsNoTracking()
            .Where(x => x.EndTs == null && x.AckTs == null && !x.Shelved && !x.Suppressed && !x.Chattering && x.StartTs <= now - minMs)
            .OrderBy(x => x.StartTs).Take(500).ToListAsync(ct);
        var results = new List<NotificationDelivery>();
        foreach (var alarm in candidates)
        {
            foreach (var (route, channel) in await MatchingRoutesAsync(db, alarm, escalation: true, ct))
            {
                if (now - alarm.StartTs < route.EscalateAfterMinutes!.Value * 60_000L) continue;
                var logs = await db.AlarmNotificationLogs.AsNoTracking().Where(x => x.AlarmEventId == alarm.Id && x.RouteId == route.Id && x.Action == "escalate").ToListAsync(ct);
                // 成功一次即止；连续失败 3 次不再重试，避免对故障通道无限重发。
                if (logs.Any(x => x.Success) || logs.Count >= 3) continue;
                results.Add(await DeliverAsync(db, alarm, route, channel, "escalate", ct));
            }
        }
        return results;
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

    private async Task<NotificationDelivery> DeliverAsync(ConfigDbContext db, AlarmEvent alarm, NotificationRoute route, NotificationChannel channel, string action, CancellationToken ct)
    {
        string? error = null;
        var recipients = SplitRecipients(route.Recipients);
        try
        {
            if (!_senders.TryGetValue(channel.Type, out var sender)) throw new InvalidOperationException("不支持的通道类型");
            var areaNames = await db.Areas.AsNoTracking().Select(a => new { a.Id, a.Name }).ToListAsync(ct);
            var ids = await AreasOfAsync(alarm, ct);
            var names = areaNames.Where(a => ids.Contains(a.Id)).Select(a => a.Name).ToList();
            await sender.SendAsync(NotificationChannelSettings.Parse(channel.SettingsJson), Secret(channel), recipients, Compose(alarm, action, route.Name, names), ct);
            Interlocked.Increment(ref _sent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            error = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            _lastError = error;
            Interlocked.Increment(ref _failed);
            _logger.LogWarning("报警通知 {Route} 经通道 {Channel} 发送失败：{Error}", route.Name, channel.Name, error);
        }

        var now = ScadaTime.NowMs();
        if (alarm.Id > 0 && await db.AlarmEvents.AnyAsync(x => x.Id == alarm.Id, ct))
        {
            db.AlarmNotificationLogs.Add(new AlarmNotificationLog { AlarmEventId = alarm.Id, RouteId = route.Id, ChannelId = channel.Id, Action = action, Ts = now, Success = error is null, Error = error });
            db.AlarmActivities.Add(new AlarmActivity
            {
                AlarmEventId = alarm.Id, Ts = now, Action = action == "escalate" ? "escalate" : "notify",
                Note = $"{(action == "escalate" ? "升级通知" : "通知")}「{route.Name}」经 {channel.Name}：{(error is null ? "已发送" : "失败 " + error)}"
            });
            await db.SaveChangesAsync(ct);
        }
        return new(route.Id, channel.Id, action, error is null, error);
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
