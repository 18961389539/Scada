using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Engine.Notifications;

namespace Scada.Engine.Tests;

public sealed class AlarmWebhookNotifierTests
{
    [Fact]
    public async Task WithoutUrlNothingIsSent()
    {
        var recorder = new Recorder();
        using var notifier = Create(recorder, url: null);

        await notifier.PublishAlarmAsync(Alarm(1, AlarmLevel.Critical), "raise");

        Assert.False(notifier.Enabled);
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task AlarmsBelowTheConfiguredLevelAreSuppressed()
    {
        var recorder = new Recorder();
        using var notifier = Create(recorder, minLevel: AlarmLevel.Critical);
        await notifier.StartAsync(default);

        await notifier.PublishAlarmAsync(Alarm(1, AlarmLevel.Warning), "raise");

        Assert.Equal(1, notifier.Suppressed);
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task RaisedAlarmIsPostedWithActionAndPayload()
    {
        var recorder = new Recorder();
        using var notifier = Create(recorder);
        await notifier.StartAsync(default);

        await notifier.PublishAlarmAsync(Alarm(7, AlarmLevel.Critical, "温度超限"), "raise");
        await WaitUntil(() => recorder.Requests.Count == 1);

        var body = recorder.Requests.Single();
        using var json = JsonDocument.Parse(body.Body);
        Assert.Equal("raise", json.RootElement.GetProperty("action").GetString());
        Assert.Equal("温度超限", json.RootElement.GetProperty("alarm").GetProperty("message").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("alarm").GetProperty("tagId").GetInt32());
        // 现场接收端未必支持 chunked，必须带明确长度与 JSON 内容类型。
        Assert.True(body.ContentLength > 0);
        Assert.Equal("application/json", body.ContentType);
        Assert.Equal(1, notifier.Sent);
    }

    [Fact]
    public async Task RepeatRaiseInsideCooldownIsSuppressedButRecoveryAlwaysSends()
    {
        var recorder = new Recorder();
        using var notifier = Create(recorder, cooldownSec: 60);
        await notifier.StartAsync(default);

        await notifier.PublishAlarmAsync(Alarm(3, AlarmLevel.Critical), "raise");
        await notifier.PublishAlarmAsync(Alarm(3, AlarmLevel.Critical), "raise");
        await WaitUntil(() => recorder.Requests.Count == 1);
        Assert.Equal(1, notifier.Suppressed);

        await notifier.PublishAlarmAsync(Alarm(3, AlarmLevel.Critical), "recover");
        await WaitUntil(() => recorder.Requests.Count == 2);

        // 恢复事件清掉冷却，下一次触发立刻上报。
        await notifier.PublishAlarmAsync(Alarm(3, AlarmLevel.Critical), "raise");
        await WaitUntil(() => recorder.Requests.Count == 3);
        Assert.Equal(3, notifier.Sent);
    }

    [Fact]
    public async Task DeliveryFailureNeverThrowsAndSurfacesAsHealthWarning()
    {
        var recorder = new Recorder { Status = HttpStatusCode.InternalServerError };
        using var notifier = Create(recorder);
        await notifier.StartAsync(default);

        for (var tag = 1; tag <= 3; tag++)
        {
            await notifier.PublishAlarmAsync(Alarm(tag, AlarmLevel.Critical), "raise");
        }

        await WaitUntil(() => notifier.Failed >= 3);
        var warning = Assert.Single(notifier.Warnings());
        Assert.Contains("报警外发连续失败", warning);
        Assert.Contains("500", notifier.LastError);
    }

    [Fact]
    public async Task QueueOverflowIsCountedInsteadOfBlockingTheAlarmPath()
    {
        var gate = new TaskCompletionSource();
        var recorder = new Recorder { Gate = gate.Task };
        using var notifier = Create(recorder, queueSize: 1);
        await notifier.StartAsync(default);

        for (var tag = 1; tag <= 5; tag++)
        {
            await notifier.PublishAlarmAsync(Alarm(tag, AlarmLevel.Critical), "raise");
        }

        Assert.True(notifier.Dropped >= 1, $"dropped={notifier.Dropped}");
        gate.SetResult();
    }

    private static AlarmWebhookNotifier Create(
        Recorder recorder, string? url = "http://collector.local/alarm", AlarmLevel minLevel = AlarmLevel.Warning,
        int cooldownSec = 0, int queueSize = 100) =>
        new(
            Options.Create(new ScadaSettings
            {
                AlarmWebhookUrl = url,
                AlarmWebhookMinLevel = minLevel,
                AlarmWebhookCooldownSec = cooldownSec,
                AlarmWebhookQueueSize = queueSize,
                AlarmWebhookTimeoutMs = 2000
            }),
            NullLogger<AlarmWebhookNotifier>.Instance,
            new HttpClient(recorder));

    private static AlarmEvent Alarm(int tagId, AlarmLevel level, string message = "超限") => new()
    {
        Id = tagId * 10,
        TagId = tagId,
        Level = level,
        Message = message,
        Value = 88.5,
        StartTs = 1_700_000_000_000
    };

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail("等待条件超时");
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public ConcurrentQueue<Captured> Requests { get; } = new();
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public Task? Gate { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Gate is not null) await Gate.WaitAsync(cancellationToken);
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new Captured(body, request.Content?.Headers.ContentLength, request.Content?.Headers.ContentType?.MediaType));
            return new HttpResponseMessage(Status);
        }
    }

    private sealed record Captured(string Body, long? ContentLength, string? ContentType);
}
