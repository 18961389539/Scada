using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Engine.Notifications;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

// ISA-18.2 报警管理：恢复延时、停用、条件抑制、抖动、暂挂到期、通知路由/升级、KPI。
public class AlarmManagementTests
{
    private static AlarmRule Rule(int offDelay = 0) => new() { Id = 10, TagId = 1, Type = AlarmRuleType.High, Threshold = 10, Level = AlarmLevel.Warning, Enabled = true, OffDelaySec = offDelay };
    private static readonly Tag T1 = new() { Id = 1, DeviceId = 1, Name = "T1" };

    [Fact]
    public void OffDelayHoldsAlarmUntilRecoveryPersists()
    {
        var evaluator = new AlarmEvaluator(); var rule = Rule(offDelay: 5);
        Assert.Single(evaluator.Evaluate(T1, new(1, 15, TagQuality.Good, 0), [rule], 0));
        Assert.Empty(evaluator.Evaluate(T1, new(1, 5, TagQuality.Good, 1000), [rule], 1000));
        // 回到报警区，恢复计时清零。
        Assert.Empty(evaluator.Evaluate(T1, new(1, 15, TagQuality.Good, 3000), [rule], 3000));
        Assert.Empty(evaluator.Evaluate(T1, new(1, 5, TagQuality.Good, 4000), [rule], 4000));
        Assert.Empty(evaluator.Evaluate(T1, new(1, 5, TagQuality.Good, 8000), [rule], 8000));
        var recovered = Assert.Single(evaluator.Evaluate(T1, new(1, 5, TagQuality.Good, 9000), [rule], 9000));
        Assert.Equal(AlarmTransitionKind.Recover, recovered.Kind);
    }

    [Fact]
    public void OutOfServiceEndsActiveAlarmAndStopsEvaluation()
    {
        var evaluator = new AlarmEvaluator(); var rule = Rule();
        Assert.Single(evaluator.Evaluate(T1, new(1, 15, TagQuality.Good, 0), [rule], 0));
        rule.OutOfService = true;
        Assert.Equal(AlarmTransitionKind.Recover, Assert.Single(evaluator.Evaluate(T1, new(1, 15, TagQuality.Good, 100), [rule], 100)).Kind);
        Assert.Empty(evaluator.Evaluate(T1, new(1, 20, TagQuality.Good, 200), [rule], 200));
    }

    [Fact]
    public async Task ConditionSuppressionFlagsRaiseAndReannunciatesWhenConditionClears()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var rule = new AlarmRule { Id = 1, TagId = 1, Type = AlarmRuleType.High, Threshold = 10, Enabled = true, SuppressTagId = 2, SuppressMode = AlarmSuppressMode.WhenOff };
        using var bus = new RealtimeBus(); var cache = new RealtimeCache(); var notifier = new Recorder();
        using var engine = new AlarmEngine(bus, new Snapshot([rule]), cache, factory, notifier, NullLogger<AlarmEngine>.Instance);
        cache.Set(new TagValue(2, 0, TagQuality.Good, ScadaTime.NowMs())); // 设备停机
        await engine.StartAsync(default);
        var value = new TagValue(1, 15, TagQuality.Good, ScadaTime.NowMs()); cache.Set(value); bus.Publish(value);
        var raised = await notifier.WaitAsync("raise");
        Assert.True(raised.Suppressed); Assert.Contains("停机", raised.SuppressReason);
        Assert.False(AlarmStates.Annunciated(raised));

        cache.Set(new TagValue(2, 1, TagQuality.Good, ScadaTime.NowMs())); // 设备运行，抑制解除
        var released = await notifier.WaitAsync("unsuppress");
        Assert.False(released.Suppressed);
        await engine.StopAsync(default);
        await using var check = factory.CreateDbContext();
        var actions = await check.AlarmActivities.OrderBy(x => x.Id).Select(x => x.Action).ToListAsync();
        Assert.Equal(["raise", "unsuppress"], actions);
    }

    [Fact]
    public async Task RepeatedRaisesWithinWindowAreMarkedChattering()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var rule = new AlarmRule { Id = 1, TagId = 1, Type = AlarmRuleType.High, Threshold = 10, Enabled = true };
        using var bus = new RealtimeBus(); var cache = new RealtimeCache(); var notifier = new Recorder();
        var settings = Options.Create(new ScadaSettings { AlarmChatterCount = 3, AlarmChatterWindowSec = 60 });
        using var engine = new AlarmEngine(bus, new Snapshot([rule]), cache, factory, notifier, NullLogger<AlarmEngine>.Instance, null, settings);
        await engine.StartAsync(default);
        foreach (var v in new[] { 15.0, 5, 15, 5, 15 }) { var value = new TagValue(1, v, TagQuality.Good, ScadaTime.NowMs()); cache.Set(value); bus.Publish(value); await Task.Delay(300); }
        await notifier.WaitCountAsync("raise", 3);
        await engine.StopAsync(default);
        await using var check = factory.CreateDbContext();
        var flags = await check.AlarmEvents.OrderBy(x => x.Id).Select(x => x.Chattering).ToListAsync();
        Assert.Equal([false, false, true], flags);
    }

    [Fact]
    public async Task ShelveNeedsReasonAndExpiresAutomatically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await SeedRuleAsync(factory);
        int eventId;
        await using (var db = factory.CreateDbContext())
        {
            var alarm = new AlarmEvent { RuleId = 1, TagId = 1, DeviceId = 1, StartTs = 1, Message = "active" };
            db.AlarmEvents.Add(alarm); await db.SaveChangesAsync(); eventId = alarm.Id;
        }
        var notifier = new Recorder(); var shelf = new AlarmShelfService(factory, notifier);
        Assert.False((await shelf.ShelveAsync(1, 30, " ", "op", 480)).Success);
        Assert.False((await shelf.ShelveAsync(1, 600, "检修", "op", 480)).Success);
        var ok = await shelf.ShelveAsync(1, 30, "检修传感器", "op", 480);
        Assert.True(ok.Success); Assert.True(shelf.IsShelved(1));
        await using (var db = factory.CreateDbContext()) Assert.True((await db.AlarmEvents.SingleAsync()).Shelved);

        Assert.Equal(0, await shelf.ExpireDueAsync(ScadaTime.NowMs()));
        Assert.Equal(1, await shelf.ExpireDueAsync(ok.Shelve!.UntilTs));
        Assert.False(shelf.IsShelved(1));
        var unshelved = await notifier.WaitAsync("unshelve");
        Assert.Equal(eventId, unshelved.Id); Assert.False(unshelved.Shelved);
        await using (var db = factory.CreateDbContext())
        {
            var record = await db.AlarmShelves.SingleAsync();
            Assert.NotNull(record.ClearedTs); Assert.Equal("system", record.ClearedBy);
            Assert.Equal(["shelve", "unshelve"], await db.AlarmActivities.OrderBy(x => x.Id).Select(x => x.Action).ToListAsync());
        }
        // 重启后从库里恢复暂挂状态。
        var again = await shelf.ShelveAsync(1, 10, "再次暂挂", "op", 480);
        var reloaded = new AlarmShelfService(factory, notifier); await reloaded.EnsureLoadedAsync();
        Assert.True(reloaded.IsShelved(1)); Assert.Equal(again.Shelve!.Id, reloaded.Active.Single().Id);
    }

    [Fact]
    public void ShiftWindowsHandleMidnightAndWeekdays()
    {
        var night = new DutyShift { StartMinute = 20 * 60, EndMinute = 8 * 60, Days = 1 << (int)DayOfWeek.Monday };
        var mondayNight = new DateTimeOffset(2026, 10, 19, 22, 0, 0, TimeSpan.Zero); // 周一
        Assert.True(AlarmNotificationDispatcher.ShiftActive(night, mondayNight));
        Assert.True(AlarmNotificationDispatcher.ShiftActive(night, mondayNight.AddHours(9)));   // 周二 07:00，仍属周一夜班
        Assert.False(AlarmNotificationDispatcher.ShiftActive(night, mondayNight.AddHours(11))); // 周二 09:00
        Assert.False(AlarmNotificationDispatcher.ShiftActive(night, mondayNight.AddDays(1)));  // 周二夜里不排班
        var day = new DutyShift { StartMinute = 8 * 60, EndMinute = 20 * 60, Days = 127 };
        Assert.True(AlarmNotificationDispatcher.ShiftActive(day, mondayNight.AddHours(-10)));
        Assert.False(AlarmNotificationDispatcher.ShiftActive(day, mondayNight));
    }

    [Fact]
    public async Task DispatcherRoutesByLevelAreaAndShiftAndEscalatesOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Areas.AddRange(new Area { Id = 2, Name = "A车间" }, new Area { Id = 3, Name = "B车间" });
            db.NotificationChannels.Add(new NotificationChannel { Id = 1, Name = "hook", Type = NotificationChannelType.Webhook, SettingsJson = "{\"url\":\"http://x/\"}", SecretProtected = "enc:tok" });
            db.DutyShifts.Add(new DutyShift { Id = 1, Name = "白班", StartMinute = 8 * 60, EndMinute = 20 * 60, Days = 127 });
            db.DutyShifts.Add(new DutyShift { Id = 2, Name = "夜班", StartMinute = 20 * 60, EndMinute = 8 * 60, Days = 127 });
            db.NotificationRoutes.AddRange(
                new NotificationRoute { Id = 1, Name = "A区", ChannelId = 1, AreaId = 2, MinLevel = AlarmLevel.Warning, OnRaise = true, OnRecover = true },
                new NotificationRoute { Id = 2, Name = "B区", ChannelId = 1, AreaId = 3, MinLevel = AlarmLevel.Warning, OnRaise = true },
                new NotificationRoute { Id = 3, Name = "全厂严重", ChannelId = 1, MinLevel = AlarmLevel.Critical, OnRaise = true },
                new NotificationRoute { Id = 4, Name = "A区夜班", ChannelId = 1, AreaId = 2, ShiftId = 2, MinLevel = AlarmLevel.Info, OnRaise = true },
                new NotificationRoute { Id = 5, Name = "A区升级", ChannelId = 1, AreaId = 2, ShiftId = 1, MinLevel = AlarmLevel.Warning, EscalateAfterMinutes = 5, OnRaise = false });
            await db.SaveChangesAsync();
        }
        var sender = new CapturingSender();
        var clock = new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero); // 白班
        var dispatcher = Dispatcher(factory, sender, () => clock);

        var alarmA = await AddAlarmAsync(factory, tagId: 1, AlarmLevel.Warning, clock.ToUnixTimeMilliseconds() - 60_000);
        var sent = await dispatcher.ProcessAsync(alarmA, "raise");
        Assert.Equal([1], sent.Select(x => x.RouteId));
        Assert.Equal("tok", sender.Calls.Single().Secret);
        Assert.Contains("A车间", sender.Calls.Single().Message.Body);
        // 重复触发通知不重发；暂挂解除后补发也不重复。
        Assert.Empty(await dispatcher.ProcessAsync(alarmA, "raise"));
        Assert.Empty(await dispatcher.ProcessAsync(alarmA, "unshelve"));

        var critB = await AddAlarmAsync(factory, tagId: 2, AlarmLevel.Critical, clock.ToUnixTimeMilliseconds());
        Assert.Equal([2, 3], (await dispatcher.ProcessAsync(critB, "raise")).Select(x => x.RouteId).Order());

        var infoA = await AddAlarmAsync(factory, tagId: 1, AlarmLevel.Info, clock.ToUnixTimeMilliseconds());
        Assert.Empty(await dispatcher.ProcessAsync(infoA, "raise")); // 白班时 Info 只走夜班路由
        clock = clock.AddHours(12);
        var infoNight = await AddAlarmAsync(factory, tagId: 1, AlarmLevel.Info, clock.ToUnixTimeMilliseconds());
        Assert.Equal([4], (await dispatcher.ProcessAsync(infoNight, "raise")).Select(x => x.RouteId));

        var shelved = await AddAlarmAsync(factory, tagId: 1, AlarmLevel.Critical, clock.ToUnixTimeMilliseconds(), shelved: true);
        Assert.Empty(await dispatcher.ProcessAsync(shelved, "raise"));

        // 升级：白班、A 区、未确认超过 5 分钟的报警只升级一次；已确认的不升级。
        clock = new DateTimeOffset(2026, 10, 19, 10, 10, 0, TimeSpan.Zero);
        await using (var db = factory.CreateDbContext())
        {
            foreach (var e in await db.AlarmEvents.Where(x => x.Id != alarmA.Id).ToListAsync()) e.AckTs = 1;
            await db.SaveChangesAsync();
        }
        Assert.Equal([5], (await dispatcher.ScanEscalationsAsync()).Select(x => x.RouteId));
        Assert.Empty(await dispatcher.ScanEscalationsAsync());
        await using (var db = factory.CreateDbContext())
        {
            Assert.Contains(await db.AlarmActivities.Where(x => x.AlarmEventId == alarmA.Id).Select(x => x.Action).ToListAsync(), x => x == "escalate");
            Assert.True(await db.AlarmNotificationLogs.AnyAsync(x => x.AlarmEventId == alarmA.Id && x.RouteId == 5 && x.Success));
        }
    }

    [Fact]
    public async Task FailedFirstNotificationIsRetriedWithBackoffSurvivesRestartAndFinallyFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await SeedHookAsync(factory, new NotificationRoute { Id = 1, Name = "首次", ChannelId = 1, MinLevel = AlarmLevel.Info, OnRaise = true });
        var sender = new CapturingSender { Fail = true };
        var clock = DateTimeOffset.UtcNow;
        var settings = new ScadaSettings { ShiftTimeZone = "UTC", NotificationMaxAttempts = 3, NotificationRetryBaseSec = 30, NotificationRetryMaxSec = 3600 };
        var dispatcher = Dispatcher(factory, sender, () => clock, settings);
        var alarm = await AddAlarmAsync(factory, 1, AlarmLevel.Warning, clock.ToUnixTimeMilliseconds());

        var first = Assert.Single(await dispatcher.ProcessAsync(alarm, "raise"));
        Assert.False(first.Success); Assert.False(first.Final); Assert.Equal(1, first.Attempt);
        Assert.Empty(await dispatcher.DrainAsync());           // 退避期内不重试
        clock = clock.AddSeconds(31);
        // 模拟进程重启：新实例从持久化发件箱继续。
        var restarted = Dispatcher(factory, sender, () => clock, settings);
        Assert.Equal(2, Assert.Single(await restarted.DrainAsync()).Attempt);
        clock = clock.AddSeconds(59);
        Assert.Empty(await restarted.DrainAsync());             // 第二次退避 60 秒
        clock = clock.AddSeconds(2);
        var last = Assert.Single(await restarted.DrainAsync());
        Assert.False(last.Success); Assert.True(last.Final); Assert.Equal(3, last.Attempt);
        clock = clock.AddHours(2);
        Assert.Empty(await restarted.DrainAsync());
        Assert.Equal(3, sender.Calls.Count);

        await using var check = factory.CreateDbContext();
        var row = await check.NotificationOutbox.SingleAsync();
        Assert.Equal(NotificationOutboxStatus.Failed, row.Status); Assert.Contains("模拟失败", row.LastError);
        var activities = await check.AlarmActivities.Where(x => x.AlarmEventId == alarm.Id).OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(["notify", "notify", "notify-failed"], activities.Select(x => x.Action));
        Assert.Contains("最终失败", activities[^1].Note);
        Assert.Equal(1, (await restarted.OutboxStatsAsync()).Failed);
        Assert.Single(restarted.Warnings());

        // 恢复：通道好了之后新报警正常发送；同一报警不会因再次触发而重复入队。
        sender.Fail = false;
        Assert.Empty(await restarted.ProcessAsync(alarm, "raise"));
        var next = await AddAlarmAsync(factory, 1, AlarmLevel.Warning, clock.ToUnixTimeMilliseconds());
        Assert.True(Assert.Single(await restarted.ProcessAsync(next, "raise")).Success);
    }

    [Fact]
    public async Task ConcurrentEscalationScansAndTriggersSendOnlyOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
        var factory = new Factory(path);
        try { await RunConcurrentAsync(factory); }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(path); } catch { } }
    }

    private static async Task RunConcurrentAsync(Factory factory)
    {
        await SeedHookAsync(factory,
            new NotificationRoute { Id = 1, Name = "升级", ChannelId = 1, MinLevel = AlarmLevel.Info, EscalateAfterMinutes = 1 },
            new NotificationRoute { Id = 2, Name = "首次", ChannelId = 1, MinLevel = AlarmLevel.Info, OnRaise = true });
        var sender = new CapturingSender { DelayMs = 100 };
        var clock = DateTimeOffset.UtcNow;
        // 两个实例模拟后台扫描与另一处同时触发（共享同一数据库）。
        var a = Dispatcher(factory, sender, () => clock); var b = Dispatcher(factory, sender, () => clock);
        var alarm = await AddAlarmAsync(factory, 1, AlarmLevel.Warning, clock.ToUnixTimeMilliseconds() - 5 * 60_000);
        await Task.WhenAll(a.ScanEscalationsAsync(), b.ScanEscalationsAsync(), a.ScanEscalationsAsync(), b.ProcessAsync(alarm, "raise"), a.ProcessAsync(alarm, "raise"));
        await Task.WhenAll(a.ScanEscalationsAsync(), b.ScanEscalationsAsync());
        Assert.Equal(2, sender.Calls.Count); // 升级 1 次 + 首次通知 1 次
        await using var check = factory.CreateDbContext();
        Assert.Equal(2, await check.NotificationOutbox.CountAsync(x => x.Status == NotificationOutboxStatus.Sent));
        Assert.Equal(1, await check.AlarmNotificationLogs.CountAsync(x => x.Action == "escalate"));
    }

    private static async Task SeedHookAsync(Factory factory, params NotificationRoute[] routes)
    {
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.Areas.Add(new Area { Id = 2, Name = "A车间" });
        db.NotificationChannels.Add(new NotificationChannel { Id = 1, Name = "hook", Type = NotificationChannelType.Webhook, SettingsJson = "{}" });
        db.NotificationRoutes.AddRange(routes);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SmsAndWebhookAdaptersSendExpectedPayloadAndHeader()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var settings = new NotificationChannelSettings { Url = "http://sms.local/send", HeaderName = "X-Api-Key" };
        await new SmsHttpNotificationSender(http).SendAsync(settings, "k-123", ["13800000000"], new("s", "高温报警", new { }), default);
        var (headers, body) = handler.Requests.Single();
        Assert.Equal("k-123", headers);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("13800000000", json.RootElement.GetProperty("phones")[0].GetString());
        Assert.Equal("高温报警", json.RootElement.GetProperty("message").GetString());

        Assert.NotNull(new NotificationChannelSettings { Url = "http://user:pw@host/" }.Validate(NotificationChannelType.Webhook));
        Assert.NotNull(new NotificationChannelSettings { Host = "smtp", From = "bad" }.Validate(NotificationChannelType.Email));
        Assert.Null(new NotificationChannelSettings { Host = "smtp", Port = 25, From = "scada@plant.local" }.Validate(NotificationChannelType.Email));
    }

    [Fact]
    public async Task EmailAdapterTalksSmtp()
    {
        using var smtp = new FakeSmtpServer();
        var settings = new NotificationChannelSettings { Host = "127.0.0.1", Port = smtp.Port, From = "scada@plant.local" };
        await new EmailNotificationSender().SendAsync(settings, null, ["duty@plant.local", "not-an-email"], new("[SCADA] 高温", "A车间 高温报警", new { }), default);
        var mail = await smtp.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("RCPT TO:<duty@plant.local>", mail);
        Assert.DoesNotContain("not-an-email", mail);
    }

    [Fact]
    public async Task KpiCountsRatesFloodsTopStandingAndOperators()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(); var factory = new Factory(connection);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        const long from = 1_000_000_000L; var to = from + 3_600_000L;
        await using (var db = factory.CreateDbContext())
        {
            // 第一个 10 分钟：12 条（洪水，阈值 10），其中 rule 1 占 8 条；之后零散 3 条；另有 1 条暂挂不计入报警量。
            for (var i = 0; i < 12; i++)
                db.AlarmEvents.Add(new AlarmEvent { RuleId = i < 8 ? 1 : 2 + i, TagId = 1, Message = i < 8 ? "泵频繁" : $"其他{i}", StartTs = from + i * 1000, EndTs = from + i * 1000 + 500, AckUser = i % 2 == 0 ? "张三" : "李四", AckTs = from + i * 1000 + 10_000 });
            for (var i = 0; i < 3; i++) db.AlarmEvents.Add(new AlarmEvent { RuleId = 50, TagId = 2, Message = "B区", StartTs = from + 20 * 60_000 + i, EndTs = from + 30 * 60_000 });
            db.AlarmEvents.Add(new AlarmEvent { RuleId = 60, TagId = 1, Message = "暂挂", StartTs = from + 5, Shelved = true, EndTs = from + 6 });
            db.AlarmEvents.Add(new AlarmEvent { RuleId = 70, TagId = 1, Message = "长期驻留", StartTs = ScadaTime.NowMs() - 30 * 3_600_000L });
            await db.SaveChangesAsync();
        }
        var kpi = new AlarmKpiService(factory, Options.Create(new ScadaSettings { AlarmFloodThreshold = 10, AlarmStandingHours = 24 }));
        var all = await kpi.ComputeAsync(from, to, 3, null, _ => true);
        Assert.Equal(16, all.Total); Assert.Equal(15, all.Annunciated); Assert.Equal(1, all.Shelved);
        Assert.Equal(15, all.PerHour); Assert.Equal(12, all.Peak10Min);
        Assert.Equal(1, all.FloodBuckets); Assert.Equal(16.67, all.PercentTimeInFlood);
        Assert.Equal("泵频繁", all.Top[0].Message); Assert.Equal(8, all.Top[0].Count); Assert.Equal(3, all.Top.Count);
        Assert.Equal("长期驻留", Assert.Single(all.Standing).Message);
        Assert.Equal(2, all.Operators.Count); Assert.Equal(6, all.Operators[0].Acks); Assert.Equal(10, all.Operators[0].AvgAckSeconds);
        Assert.Equal(7.5, all.PerOperatorHour);
        // 可见性过滤（区域授权）：只看 2 号测点。
        var areaB = await kpi.ComputeAsync(from, to, 10, null, x => x.TagId == 2);
        Assert.Equal(3, areaB.Total); Assert.Empty(areaB.Standing); Assert.Equal(0, areaB.FloodBuckets);
    }

    // ---- helpers ----

    private static AlarmNotificationDispatcher Dispatcher(Factory factory, INotificationSender sender, Func<DateTimeOffset> clock, ScadaSettings? settings = null) => new(
        factory,
        new AccessModelProvider(_ => Task.FromResult(AccessModel.Build([(1, 2), (2, 3)], [(1, TagKind.Physical, 1, null), (2, TagKind.Physical, 2, null)], [], [], [], 0))),
        new FakeProtector(), [sender], Options.Create(settings ?? new ScadaSettings { ShiftTimeZone = "UTC" }), NullLogger<AlarmNotificationDispatcher>.Instance, clock);

    private static async Task<AlarmEvent> AddAlarmAsync(Factory factory, int tagId, AlarmLevel level, long start, bool shelved = false)
    {
        await using var db = factory.CreateDbContext();
        var alarm = new AlarmEvent { TagId = tagId, Level = level, StartTs = start, Message = $"测点{tagId}报警", Shelved = shelved };
        db.AlarmEvents.Add(alarm); await db.SaveChangesAsync(); return alarm;
    }

    private static async Task SeedRuleAsync(Factory factory)
    {
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.Devices.Add(new Device { Id = 1, Name = "d" }); db.Tags.Add(new Tag { Id = 1, DeviceId = 1, Name = "t" });
        db.AlarmRules.Add(new AlarmRule { Id = 1, TagId = 1, Threshold = 10 }); await db.SaveChangesAsync();
    }

    private sealed class Factory : IDbContextFactory<ConfigDbContext>
    {
        private readonly SqliteConnection? _connection; private readonly string? _cs;
        public Factory(SqliteConnection connection) => _connection = connection;
        // 文件库：每个上下文独立连接，用于并发场景。
        public Factory(string path) => _cs = $"Data Source={path};Default Timeout=30;Pooling=False";
        public ConfigDbContext CreateDbContext() => new(_connection is not null
            ? new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(_connection).Options
            : new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(_cs!).Options);
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plain) => "enc:" + plain;
        public string Unprotect(string value) => value.StartsWith("enc:") ? value[4..] : throw new InvalidOperationException();
    }

    private sealed class CapturingSender : INotificationSender
    {
        public bool Fail { get; set; }
        public int DelayMs { get; set; }
        public ConcurrentQueue<(string? Secret, IReadOnlyList<string> To, NotificationMessage Message)> Calls { get; } = new();
        public NotificationChannelType Type => NotificationChannelType.Webhook;
        public async Task SendAsync(NotificationChannelSettings settings, string? secret, IReadOnlyList<string> recipients, NotificationMessage message, CancellationToken ct)
        {
            Calls.Enqueue((secret, recipients, message));
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            if (Fail) throw new HttpRequestException("模拟失败");
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(string? Header, string Body)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Headers.TryGetValues("X-Api-Key", out var v) ? v.Single() : null, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Recorder : IAlarmNotifier
    {
        private readonly ConcurrentQueue<(string Action, AlarmEvent Alarm)> _events = new();
        public Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default) { _events.Enqueue((action, alarm)); return Task.CompletedTask; }
        public async Task<AlarmEvent> WaitAsync(string action)
        {
            for (var i = 0; i < 100; i++) { var hit = _events.FirstOrDefault(x => x.Action == action); if (hit.Alarm is not null) return hit.Alarm; await Task.Delay(50); }
            throw new TimeoutException(action);
        }
        public async Task WaitCountAsync(string action, int count)
        {
            for (var i = 0; i < 100 && _events.Count(x => x.Action == action) < count; i++) await Task.Delay(50);
            Assert.Equal(count, _events.Count(x => x.Action == action));
        }
    }

    private sealed class Snapshot(IReadOnlyList<AlarmRule> rules) : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => [new() { Id = 1, PollIntervalMs = 100 }];
        public IReadOnlyList<Tag> Tags => [new() { Id = 1, DeviceId = 1, Name = "温度" }, new() { Id = 2, DeviceId = 1, Name = "运行" }];
        public IReadOnlyList<AlarmRule> AlarmRules => rules;
        public int Version => 1; public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag tag) { tag = Tags.FirstOrDefault(x => x.Id == id)!; return tag is not null; }
        public bool TryGetDevice(int id, out Device device) { device = Devices[0]; return id == 1; }
    }

    // 只够 SmtpClient 完成一次投递的极简 SMTP 服务。
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public TaskCompletionSource<string> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public FakeSmtpServer() { _listener.Start(); _ = RunAsync(); }
        private async Task RunAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                var log = new StringBuilder();
                await writer.WriteLineAsync("220 fake");
                while (await reader.ReadLineAsync() is { } line)
                {
                    log.AppendLine(line);
                    if (line.StartsWith("EHLO") || line.StartsWith("HELO")) await writer.WriteLineAsync("250 ok");
                    else if (line.StartsWith("DATA"))
                    {
                        await writer.WriteLineAsync("354 go");
                        while (await reader.ReadLineAsync() is { } data && data != ".") log.AppendLine(data);
                        await writer.WriteLineAsync("250 queued");
                    }
                    else if (line.StartsWith("QUIT")) { await writer.WriteLineAsync("221 bye"); break; }
                    else await writer.WriteLineAsync("250 ok");
                }
                Received.TrySetResult(log.ToString());
            }
            catch (Exception ex) { Received.TrySetException(ex); }
        }
        public void Dispose() => _listener.Stop();
    }
}
