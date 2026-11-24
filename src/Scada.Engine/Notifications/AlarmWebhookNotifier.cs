using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
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

public interface IAlarmWebhookStatus
{
    bool Enabled { get; }
    long Sent { get; }
    long Suppressed { get; }
    long Failed { get; }
    long Dropped { get; }
    string? LastError { get; }
    // 连续失败达到阈值时给出告警，供 /health 展示；恢复后自动消失。
    IReadOnlyList<string> Warnings();
}

/// <summary>
/// 旧版全局 AlarmWebhookUrl 与分发器共用发件箱时的投递口。
/// </summary>
public interface ILegacyAlarmWebhookDelivery
{
    bool Enabled { get; }
    static string DedupKey(string action, int alarmId) => $"{AlarmWebhookNotifier.LegacyDedupPrefix}{action}:{alarmId}";
    bool IsLegacy(NotificationOutbox item);
    Task<string?> DeliverAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken);
    void RememberSuccess(AlarmEvent alarm);
    void RememberFailure();
}

// 报警外发：站内推送之外把报警投递到 webhook（企业微信/钉钉/自建网关均可）。
// 采集侧只入队不等待网络，投递失败不影响报警本身；同测点同等级在成功投递后的冷却期内只发一次。
// 内存队列满或发送失败时写入与 AlarmNotificationDispatcher 同一套发件箱，不再把通知丢掉。
public sealed class AlarmWebhookNotifier : BackgroundService, IAlarmNotifier, IAlarmWebhookStatus, ILegacyAlarmWebhookDelivery
{
    public const string LegacyDedupPrefix = "legacy-webhook:";
    private const int FailureWarnThreshold = 3;

    // 中文报警文案不转义成 \uXXXX：接收端多为人工阅读的群消息或简单解析器，转义后不可读。
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ScadaSettings _settings;
    private readonly ILogger<AlarmWebhookNotifier> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Channel<Delivery> _queue;
    private readonly ConcurrentDictionary<string, long> _lastSent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly IDbContextFactory<RuntimeDbContext>? _runtime;
    private readonly IDbContextFactory<ConfigDbContext>? _config;
    private readonly AccessModelProvider? _access;
    private readonly Func<DateTimeOffset> _clock;

    private long _sent, _suppressed, _failed, _dropped, _consecutiveFailures, _lastFailureTs, _lastSuccessTs;
    private string? _lastError;
    private long _lastFailureLogTs;

    private readonly record struct Delivery(string Action, AlarmEvent Alarm, bool FromOutbox);

    public AlarmWebhookNotifier(
        IOptions<ScadaSettings> options,
        ILogger<AlarmWebhookNotifier> logger,
        HttpClient? http = null,
        IDbContextFactory<RuntimeDbContext>? runtime = null,
        IDbContextFactory<ConfigDbContext>? config = null,
        AccessModelProvider? access = null,
        Func<DateTimeOffset>? clock = null)
    {
        _settings = options.Value;
        _logger = logger;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _runtime = runtime;
        _config = config;
        _access = access;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        // Wait 模式下 TryWrite 满时返回 false，调用方改写入发件箱；DropWrite 会返回 true，丢包就统计不到。
        _queue = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(Math.Max(_settings.AlarmWebhookQueueSize, 1))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
    }

    public bool Enabled => Uri.TryCreate(_settings.AlarmWebhookUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    public long Sent => Interlocked.Read(ref _sent);
    public long Suppressed => Interlocked.Read(ref _suppressed);
    public long Failed => Interlocked.Read(ref _failed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public string? LastError => _lastError;

    public async Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return;
        // 暂挂/抑制解除时仍活动未确认的报警按新触发外发；暂挂、抑制、抖动中的报警不外发。
        if (action is "unshelve" or "unsuppress")
        {
            if (alarm.EndTs is not null || alarm.AckTs is not null) return;
            action = "raise";
        }
        if (action is not ("raise" or "recover" or "ack")) return;
        if (action is "raise" or "recover" && !AlarmStates.Annunciated(alarm)) { Interlocked.Increment(ref _suppressed); return; }
        if (alarm.Level < _settings.AlarmWebhookMinLevel) { Interlocked.Increment(ref _suppressed); return; }

        if (await CoveredByRouteAsync(alarm, action, cancellationToken))
        {
            Interlocked.Increment(ref _suppressed);
            return;
        }

        var key = CooldownKey(alarm);
        if (action == "recover")
        {
            // 恢复事件一定要发，并清掉冷却，让下一次再触发能立刻上报。
            _lastSent.TryRemove(key, out _);
        }
        else if (!ShouldAttempt(key)) return;

        var delivery = new Delivery(action, alarm, FromOutbox: false);
        if (_queue.Writer.TryWrite(delivery))
        {
            _inFlight[key] = 1;
            return;
        }

        if (await PersistAsync(alarm, action, cancellationToken))
        {
            _inFlight[key] = 1;
            return;
        }

        _inFlight.TryRemove(key, out _);
        Interlocked.Increment(ref _dropped);
    }

    public IReadOnlyList<string> Warnings()
    {
        if (!Enabled && Interlocked.Read(ref _dropped) == 0) return Array.Empty<string>();
        var warnings = new List<string>();
        var dropped = Interlocked.Read(ref _dropped);
        if (dropped > 0)
            warnings.Add($"报警外发有 {dropped} 条在写入发件箱失败后丢失");
        var failures = Interlocked.Read(ref _consecutiveFailures);
        if (Enabled && failures >= FailureWarnThreshold)
            warnings.Add($"报警外发连续失败 {failures} 次：{_lastError ?? "未知错误"}");
        return warnings;
    }

    bool ILegacyAlarmWebhookDelivery.IsLegacy(NotificationOutbox item) =>
        item.ReportRunId is null && (item.DedupKey.StartsWith(LegacyDedupPrefix, StringComparison.Ordinal) || (item.RouteId == 0 && item.ChannelId == 0 && item.Action is "raise" or "recover" or "ack"));

    public async Task<string?> DeliverAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken)
    {
        if (!Enabled) return "未配置 AlarmWebhookUrl";
        var key = CooldownKey(alarm);
        if (action != "recover" && InCooldown(key))
        {
            Interlocked.Increment(ref _suppressed);
            return null;
        }
        try
        {
            await SendAsync(new Delivery(action, alarm, FromOutbox: true), cancellationToken);
            RememberSuccess(alarm);
            if (action == "recover") _lastSent.TryRemove(key, out _);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RememberFailure();
            RecordFailure(ex);
            return ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
        }
    }

    public void RememberSuccess(AlarmEvent alarm)
    {
        var key = CooldownKey(alarm);
        _lastSent[key] = ScadaTime.NowMs();
        _inFlight.TryRemove(key, out _);
    }

    public void RememberFailure()
    {
        // 失败不记冷却，允许发件箱重试以及同测点再次入队。
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation("未配置 Scada:AlarmWebhookUrl，报警只做站内推送");
            return;
        }

        var drain = Task.Run(() => DrainLoopAsync(stoppingToken), stoppingToken);
        try
        {
            await foreach (var delivery in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await SendAsync(delivery, stoppingToken);
                    RememberSuccess(delivery.Alarm);
                    if (delivery.Action == "recover") _lastSent.TryRemove(CooldownKey(delivery.Alarm), out _);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _inFlight.TryRemove(CooldownKey(delivery.Alarm), out _);
                    RememberFailure();
                    RecordFailure(ex);
                    if (!await PersistAsync(delivery.Alarm, delivery.Action, CancellationToken.None))
                        Interlocked.Increment(ref _dropped);
                }
            }
        }
        catch (OperationCanceledException) { }
        try { await drain; } catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        base.Dispose();
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await DrainDueAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "旧版 webhook 发件箱重试失败"); }
        }
    }

    // 本进程也能抽出到期的旧版 webhook 条目；与分发器共用同一套抢占/租约，并发时只有一方投递。
    public async Task DrainDueAsync(CancellationToken ct = default)
    {
        if (_runtime is null || !Enabled) return;
        var now = _clock().ToUnixTimeMilliseconds();
        List<long> due;
        await using (var db = await _runtime.CreateDbContextAsync(ct))
        {
            due = await db.NotificationOutbox.AsNoTracking()
                .Where(x => x.ReportRunId == null && x.RouteId == 0 && x.ChannelId == 0
                            && (x.Status == NotificationOutboxStatus.Pending && x.NextAttemptTs <= now
                                || x.Status == NotificationOutboxStatus.Sending && x.LeaseUntilTs < now)
                            && (x.DedupKey.StartsWith(LegacyDedupPrefix) || x.Action == "raise" || x.Action == "recover" || x.Action == "ack"))
                .OrderBy(x => x.NextAttemptTs).Select(x => x.Id).Take(50).ToListAsync(ct);
        }
        foreach (var id in due)
            await DeliverOutboxAsync(id, now, ct);
    }

    private async Task DeliverOutboxAsync(long id, long now, CancellationToken ct)
    {
        if (_runtime is null) return;
        await using var runtime = await _runtime.CreateDbContextAsync(ct);
        var lease = now + AlarmNotificationDispatcher.LeaseMs;
        var claimed = await runtime.NotificationOutbox
            .Where(x => x.Id == id && ((x.Status == NotificationOutboxStatus.Pending && x.NextAttemptTs <= now) || (x.Status == NotificationOutboxStatus.Sending && x.LeaseUntilTs < now)))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, NotificationOutboxStatus.Sending).SetProperty(x => x.LeaseUntilTs, lease).SetProperty(x => x.Attempts, x => x.Attempts + 1), ct);
        if (claimed != 1) return;
        var item = await runtime.NotificationOutbox.SingleAsync(x => x.Id == id, ct);
        var alarm = item.AlarmEventId is int alarmId
            ? await runtime.AlarmEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == alarmId, ct)
            : null;
        string? error = alarm is null ? "报警已删除" : await DeliverAsync(alarm, item.Action, ct);
        var finalNow = _clock().ToUnixTimeMilliseconds();
        item.LeaseUntilTs = null;
        item.LastError = error;
        if (error is null)
        {
            item.Status = NotificationOutboxStatus.Sent;
            item.SentTs = finalNow;
            item.FinishedTs = finalNow;
        }
        else if (alarm is null || item.Attempts >= item.MaxAttempts)
        {
            item.Status = NotificationOutboxStatus.Failed;
            item.FinishedTs = finalNow;
        }
        else
        {
            item.Status = NotificationOutboxStatus.Pending;
            var backoff = Math.Min(Math.Max(_settings.NotificationRetryBaseSec, 0) * Math.Pow(2, item.Attempts - 1), Math.Max(_settings.NotificationRetryMaxSec, 0));
            item.NextAttemptTs = finalNow + (long)(backoff * 1000);
        }
        await runtime.SaveChangesAsync(ct);
    }

    private bool ShouldAttempt(string key)
    {
        if (InCooldown(key) || _inFlight.ContainsKey(key))
        {
            Interlocked.Increment(ref _suppressed);
            return false;
        }
        return true;
    }

    private bool InCooldown(string key)
    {
        var now = ScadaTime.NowMs();
        var cooldown = Math.Max(_settings.AlarmWebhookCooldownSec, 0) * 1000L;
        return cooldown > 0 && _lastSent.TryGetValue(key, out var last) && now - last < cooldown;
    }

    private static string CooldownKey(AlarmEvent alarm) => $"{alarm.TagId?.ToString() ?? "-"}:{(int)alarm.Level}";

    private async Task SendAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(_settings.AlarmWebhookTimeoutMs, 500)));
        var payload = new
        {
            type = "alarm",
            action = delivery.Action,
            sentAt = DateTimeOffset.UtcNow,
            alarm = delivery.Alarm
        };

        // 显式写出 Content-Length：现场接收端（PLC 网关、老式 HTTP 服务）未必支持 chunked 传输。
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        using var content = new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await _http.PostAsync(_settings.AlarmWebhookUrl, content, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"webhook 返回 {(int)response.StatusCode}");
        }

        Interlocked.Increment(ref _sent);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _lastSuccessTs, ScadaTime.NowMs());
    }

    private void RecordFailure(Exception ex)
    {
        Interlocked.Increment(ref _failed);
        var failures = Interlocked.Increment(ref _consecutiveFailures);
        _lastError = ex.Message;
        Interlocked.Exchange(ref _lastFailureTs, ScadaTime.NowMs());

        // 故障持续时按分钟限流，避免把日志刷爆。
        var now = ScadaTime.NowMs();
        var lastLog = Interlocked.Read(ref _lastFailureLogTs);
        if (failures == 1 || now - lastLog > 60_000)
        {
            Interlocked.Exchange(ref _lastFailureLogTs, now);
            _logger.LogWarning("报警外发失败（连续 {Failures} 次）：{Error}", failures, ex.Message);
        }
    }

    private async Task<bool> PersistAsync(AlarmEvent alarm, string action, CancellationToken ct)
    {
        if (_runtime is null || alarm.Id <= 0) return false;
        var key = ILegacyAlarmWebhookDelivery.DedupKey(action, alarm.Id);
        await using var db = await _runtime.CreateDbContextAsync(ct);
        if (await db.NotificationOutbox.AnyAsync(x => x.DedupKey == key, ct)) return true;
        var now = _clock().ToUnixTimeMilliseconds();
        db.NotificationOutbox.Add(new NotificationOutbox
        {
            DedupKey = key,
            AlarmEventId = alarm.Id,
            RouteId = 0,
            ChannelId = 0,
            Action = action,
            MaxAttempts = Math.Max(_settings.NotificationMaxAttempts, 1),
            NextAttemptTs = now,
            CreatedTs = now
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return true; // 并发入队撞上唯一约束：已有人入队
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "报警 webhook 写入发件箱失败");
            return false;
        }
    }

    private async Task<bool> CoveredByRouteAsync(AlarmEvent alarm, string action, CancellationToken ct)
    {
        if (_config is null || !Enabled) return false;
        try
        {
            await using var db = await _config.CreateDbContextAsync(ct);
            var channels = await db.NotificationChannels.AsNoTracking()
                .Where(x => x.Enabled && x.Type == NotificationChannelType.Webhook)
                .ToListAsync(ct);
            var ids = channels.Where(c => SameWebhookUrl(NotificationChannelSettings.Parse(c.SettingsJson).Url, _settings.AlarmWebhookUrl))
                .Select(c => c.Id).ToHashSet();
            if (ids.Count == 0) return false;
            var flag = action == "recover" ? "recover" : action == "raise" ? "raise" : null;
            if (flag is null) return false; // ack 没有对应路由动作，旧版通道单独发送
            var routes = await db.NotificationRoutes.AsNoTracking()
                .Where(x => x.Enabled && ids.Contains(x.ChannelId) && (flag == "raise" ? x.OnRaise : x.OnRecover))
                .ToListAsync(ct);
            if (routes.Count == 0) return false;
            var shifts = await db.DutyShifts.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            IReadOnlyList<int> areas = [];
            if (_access is not null && alarm.TagId is int tag)
                areas = (await _access.GetAsync(ct)).AreasOfTag(tag);
            else if (_access is not null && alarm.DeviceId is int device)
            {
                var model = await _access.GetAsync(ct);
                areas = model.DeviceAreas.TryGetValue(device, out var area) ? [area] : [Area.DefaultId];
            }
            var local = AlarmNotificationComposer.ToShiftTime(_clock(), _settings.ShiftTimeZone);
            return routes.Any(r =>
                alarm.Level >= r.MinLevel
                && (r.AreaId is not int area || areas.Contains(area))
                && (r.ShiftId is not int s || (shifts.TryGetValue(s, out var shift) && AlarmNotificationDispatcher.ShiftActive(shift, local))));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "判断旧版 webhook 是否与路由通道重复时失败，继续按旧版通道发送");
            return false;
        }
    }

    internal static bool SameWebhookUrl(string? left, string? right)
    {
        static string? Norm(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
            var builder = new UriBuilder(uri) { Fragment = "" };
            var text = builder.Uri.ToString();
            return text.TrimEnd('/');
        }
        var a = Norm(left);
        var b = Norm(right);
        return a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
