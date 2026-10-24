using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;

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

// 报警外发：站内推送之外把报警投递到 webhook（企业微信/钉钉/自建网关均可）。
// 采集侧只入队不等待网络，投递失败不影响报警本身；同测点同等级在冷却期内只发一次。
public sealed class AlarmWebhookNotifier : BackgroundService, IAlarmNotifier, IAlarmWebhookStatus
{
    // 中文报警文案不转义成 \uXXXX：接收端多为人工阅读的群消息或简单解析器，转义后不可读。
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private const int FailureWarnThreshold = 3;

    private readonly ScadaSettings _settings;
    private readonly ILogger<AlarmWebhookNotifier> _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Channel<Delivery> _queue;
    private readonly ConcurrentDictionary<string, long> _lastSent = new(StringComparer.Ordinal);

    private long _sent, _suppressed, _failed, _dropped, _consecutiveFailures, _lastFailureTs, _lastSuccessTs;
    private string? _lastError;
    private long _lastFailureLogTs;

    private readonly record struct Delivery(string Action, AlarmEvent Alarm);

    public AlarmWebhookNotifier(IOptions<ScadaSettings> options, ILogger<AlarmWebhookNotifier> logger, HttpClient? http = null)
    {
        _settings = options.Value;
        _logger = logger;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        // Wait 模式下 TryWrite 满时返回 false，调用方不入队即返回；DropWrite 会返回 true，丢包就统计不到。
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

    public Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default)
    {
        if (!Enabled || action == "change") return Task.CompletedTask;
        if (alarm.Level < _settings.AlarmWebhookMinLevel) { Interlocked.Increment(ref _suppressed); return Task.CompletedTask; }

        var key = $"{alarm.TagId?.ToString() ?? "-"}:{(int)alarm.Level}";
        if (action == "recover")
        {
            // 恢复事件一定要发，并清掉冷却，让下一次再触发能立刻上报。
            _lastSent.TryRemove(key, out _);
        }
        else if (!ShouldSend(key)) return Task.CompletedTask;

        if (!_queue.Writer.TryWrite(new Delivery(action, alarm)))
        {
            Interlocked.Increment(ref _dropped);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<string> Warnings()
    {
        if (!Enabled) return Array.Empty<string>();
        var failures = Interlocked.Read(ref _consecutiveFailures);
        if (failures < FailureWarnThreshold) return Array.Empty<string>();
        return new[] { $"报警外发连续失败 {failures} 次：{_lastError ?? "未知错误"}" };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation("未配置 Scada:AlarmWebhookUrl，报警只做站内推送");
            return;
        }

        await foreach (var delivery in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SendAsync(delivery, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
        }
    }

    public override void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        base.Dispose();
    }

    private bool ShouldSend(string key)
    {
        var now = ScadaTime.NowMs();
        var cooldown = Math.Max(_settings.AlarmWebhookCooldownSec, 0) * 1000L;
        if (_lastSent.TryGetValue(key, out var last) && now - last < cooldown)
        {
            Interlocked.Increment(ref _suppressed);
            return false;
        }

        _lastSent[key] = now;
        return true;
    }

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
}
