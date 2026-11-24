using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Data;
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
        Assert.Contains(notifier.Warnings(), x => x.Contains("报警外发连续失败"));
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
        Assert.Contains(notifier.Warnings(), x => x.Contains("写入发件箱失败后丢失"));
        gate.SetResult();
    }

    [Fact]
    public async Task FailedSendDoesNotStartCooldown()
    {
        await using var connection = TestRuntime.Open();
        var runtime = TestRuntime.Factory(connection);
        await SeedAlarmsAsync(runtime, 3);
        var recorder = new Recorder { Status = HttpStatusCode.InternalServerError };
        using var notifier = Create(recorder, cooldownSec: 60, runtime: runtime);
        await notifier.StartAsync(default);

        await notifier.PublishAlarmAsync(Alarm(3, AlarmLevel.Critical), "raise");
        await WaitUntil(() => notifier.Failed >= 1);
        await notifier.PublishAlarmAsync(Alarm(3, AlarmLevel.Critical), "raise");
        await WaitUntil(() => notifier.Failed >= 2);

        Assert.Equal(0, notifier.Suppressed);
        Assert.Equal(0, notifier.Dropped);
        await using var db = runtime.CreateDbContext();
        Assert.True(await db.NotificationOutbox.AnyAsync(x => x.DedupKey == "legacy-webhook:raise:30"));
    }

    [Fact]
    public async Task QueueOverflowAndSendFailurePersistToOutbox()
    {
        await using var connection = TestRuntime.Open();
        var runtime = TestRuntime.Factory(connection);
        await SeedAlarmsAsync(runtime, 1, 2, 3, 4, 5);
        var gate = new TaskCompletionSource();
        var recorder = new Recorder { Gate = gate.Task, Status = HttpStatusCode.InternalServerError };
        using var notifier = Create(recorder, queueSize: 1, runtime: runtime);
        await notifier.StartAsync(default);

        for (var tag = 1; tag <= 5; tag++)
            await notifier.PublishAlarmAsync(Alarm(tag, AlarmLevel.Critical), "raise");

        Assert.Equal(0, notifier.Dropped);
        await using (var db = runtime.CreateDbContext())
            Assert.True(await db.NotificationOutbox.CountAsync() >= 1);

        gate.SetResult();
        await WaitUntil(() => notifier.Failed >= 1);
        await notifier.DrainDueAsync();
        await using (var db = runtime.CreateDbContext())
        {
            Assert.True(await db.NotificationOutbox.AnyAsync());
            Assert.DoesNotContain(await db.NotificationOutbox.ToListAsync(), x => x.Status == "Sent");
        }
    }

    [Fact]
    public async Task SameUrlRouteSuppressesLegacyWebhook()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options;
        await using (var db = new ConfigDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.NotificationChannels.Add(new NotificationChannel { Id = 1, Name = "hook", Type = NotificationChannelType.Webhook, Enabled = true, SettingsJson = """{"url":"http://collector.local/alarm"}""" });
            db.NotificationRoutes.Add(new NotificationRoute { Id = 1, Name = "all", ChannelId = 1, Enabled = true, MinLevel = AlarmLevel.Warning, OnRaise = true });
            await db.SaveChangesAsync();
        }
        var factory = new ConfigFactory(options);
        var recorder = new Recorder();
        using var notifier = Create(recorder, config: factory);
        await notifier.StartAsync(default);
        await notifier.PublishAlarmAsync(Alarm(1, AlarmLevel.Critical), "raise");
        await Task.Delay(200);
        Assert.Equal(1, notifier.Suppressed);
        Assert.Empty(recorder.Requests);
    }

    private static AlarmWebhookNotifier Create(
        Recorder recorder, string? url = "http://collector.local/alarm", AlarmLevel minLevel = AlarmLevel.Warning,
        int cooldownSec = 0, int queueSize = 100, IDbContextFactory<RuntimeDbContext>? runtime = null, IDbContextFactory<ConfigDbContext>? config = null) =>
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
            new HttpClient(recorder),
            runtime,
            config);

    private sealed class ConfigFactory(DbContextOptions<ConfigDbContext> options) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(options);
        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private static async Task SeedAlarmsAsync(IDbContextFactory<RuntimeDbContext> runtime, params int[] tagIds)
    {
        await using var db = runtime.CreateDbContext();
        foreach (var tagId in tagIds)
            db.AlarmEvents.Add(new AlarmEvent { Id = tagId * 10, TagId = tagId, Level = AlarmLevel.Critical, Message = "超限", StartTs = 1_700_000_000_000 });
        await db.SaveChangesAsync();
    }

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
