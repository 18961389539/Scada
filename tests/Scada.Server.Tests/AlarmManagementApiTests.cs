using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Notifications;

namespace Scada.Server.Tests;

// 报警管理接口：暂挂/停用/备注时间线/通知配置/KPI 均按区域授权，配置变更有差分审计，凭据加密脱敏。
public sealed class AlarmManagementApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public AlarmManagementApiTests(AuthorizationTests.Site site) => _site = site;

    private async Task<int> RuleAsync(int tagId)
    {
        var admin = await _site.ClientAsync("admin");
        var response = await admin.PostAsJsonAsync("/api/alarm-rules", new { tagId, type = "High", threshold = 1000, level = "Warning", delaySec = 0, recoverDeadband = 0, enabled = true, message = "规则" + Guid.NewGuid().ToString("N")[..4] });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<int> ActiveAlarmAsync(int tagId, int? ruleId, long? start = null, Scada.Core.Enums.AlarmLevel level = Scada.Core.Enums.AlarmLevel.Warning)
    {
        await using var db = await Factory().CreateDbContextAsync();
        var alarm = new AlarmEvent { TagId = tagId, RuleId = ruleId, DeviceId = tagId == _site.TagA ? _site.DeviceA : _site.DeviceB, Level = level, Message = "活动报警", StartTs = start ?? ScadaTime.NowMs() };
        db.AlarmEvents.Add(alarm); await db.SaveChangesAsync(); return alarm.Id;
    }

    private IDbContextFactory<ConfigDbContext> Factory() => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();

    [Fact]
    public async Task ShelveNeedsReasonIsAreaScopedAuditedAndListedPerArea()
    {
        var ruleA = await RuleAsync(_site.TagA); var ruleB = await RuleAsync(_site.TagB);
        var alarmA = await ActiveAlarmAsync(_site.TagA, ruleA);
        var op = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/alarm-rules/{ruleA}/shelve", new { minutes = 30, reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/alarm-rules/{ruleA}/shelve", new { minutes = 100000, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/alarm-rules/{ruleA}/shelve", new { minutes = 30, reason = "仪表校验" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/alarm-rules/{ruleB}/shelve", new { minutes = 30, reason = "越权" })).StatusCode);
        Assert.StartsWith("拒绝", (await _site.LastLogAsync(x => x.UserName == "opA" && x.Action == $"暂挂报警规则 #{ruleB}"))!.Result);
        var audit = await _site.LastLogAsync(x => x.UserName == "opA" && x.Action == $"暂挂报警规则 #{ruleA}");
        Assert.Contains("仪表校验", audit!.Details);

        var listed = await op.GetFromJsonAsync<JsonElement[]>($"/api/alarms?live=true");
        var row = listed!.Single(x => x.GetProperty("id").GetInt32() == alarmA);
        Assert.True(row.GetProperty("shelved").GetBoolean()); Assert.Equal("Unack", row.GetProperty("state").GetString());

        var admin = await _site.ClientAsync("admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/alarm-rules/{ruleB}/shelve", new { minutes = 10, reason = "B区检修" })).StatusCode);
        var shelves = await op.GetFromJsonAsync<JsonElement[]>("/api/alarm-shelves");
        Assert.Contains(shelves!, x => x.GetProperty("ruleId").GetInt32() == ruleA);
        Assert.DoesNotContain(shelves!, x => x.GetProperty("ruleId").GetInt32() == ruleB);

        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/alarm-rules/{ruleA}/unshelve", new { reason = "校验完成" })).StatusCode);
        var timeline = await op.GetFromJsonAsync<JsonElement>($"/api/alarms/{alarmA}/timeline");
        var actions = timeline.GetProperty("activities").EnumerateArray().Select(x => x.GetProperty("action").GetString()).ToArray();
        Assert.Equal(new[] { "shelve", "unshelve" }, actions);
        Assert.False(timeline.GetProperty("alarm").GetProperty("shelved").GetBoolean());
    }

    [Fact]
    public async Task AckNotesBuildTimelineAndCrossAreaNotesAreRejected()
    {
        var alarmA = await ActiveAlarmAsync(_site.TagA, null);
        var alarmB = await ActiveAlarmAsync(_site.TagB, null);
        var op = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/alarms/{alarmA}/ack", new { note = "已通知巡检" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/alarms/{alarmA}/notes", new { note = "现场确认阀门卡涩" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/alarms/{alarmA}/notes", new { note = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/alarms/{alarmB}/notes", new { note = "越权" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/alarms/{alarmB}/timeline")).StatusCode);
        var timeline = await op.GetFromJsonAsync<JsonElement>($"/api/alarms/{alarmA}/timeline");
        var items = timeline.GetProperty("activities").EnumerateArray().Select(x => (x.GetProperty("action").GetString(), x.GetProperty("note").GetString(), x.GetProperty("user").GetString())).ToArray();
        Assert.Equal(("ack", "已通知巡检", "opA"), items[0]);
        Assert.Equal(("comment", "现场确认阀门卡涩", "opA"), items[1]);
        Assert.Equal("Acked", timeline.GetProperty("alarm").GetProperty("state").GetString());
        // 无请求体的旧式确认仍可用。
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsync($"/api/alarms/{await ActiveAlarmAsync(_site.TagA, null)}/ack", null)).StatusCode);
    }

    [Fact]
    public async Task OutOfServiceNeedsConfigureAndReasonAndWritesDiff()
    {
        var ruleA = await RuleAsync(_site.TagA);
        var op = await _site.ClientAsync("opA"); var cfg = await _site.ClientAsync("cfgA");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/alarm-rules/{ruleA}/out-of-service", new { outOfService = true, reason = "检修" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cfg.PostAsJsonAsync($"/api/alarm-rules/{ruleA}/out-of-service", new { outOfService = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cfg.PostAsJsonAsync($"/api/alarm-rules/{ruleA}/out-of-service", new { outOfService = true, reason = "传感器更换" })).StatusCode);
        var log = await _site.LastLogAsync(x => x.UserName == "cfgA" && x.Action == $"停用报警规则 #{ruleA}");
        using var diff = JsonDocument.Parse(log!.Details!);
        Assert.True(diff.RootElement.GetProperty("changes").GetProperty("outOfService").GetProperty("new").GetBoolean());
        Assert.Equal("传感器更换", diff.RootElement.GetProperty("changes").GetProperty("outOfServiceReason").GetProperty("new").GetString());
        // 条件抑制引用无权区域的测点被拒绝。
        var bad = await cfg.PutAsJsonAsync($"/api/alarm-rules/{ruleA}", new { tagId = _site.TagA, type = "High", threshold = 1, level = "Warning", delaySec = 0, recoverDeadband = 0, enabled = true, suppressTagId = _site.TagB, suppressMode = "WhenOff" });
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
    }

    [Fact]
    public async Task ChannelSecretsAreEncryptedMaskedAndNeverAudited()
    {
        var admin = await _site.ClientAsync("admin");
        var create = await admin.PostAsJsonAsync("/api/notification-channels", new { name = "企业微信" + Guid.NewGuid().ToString("N")[..4], type = "Webhook", enabled = true, settings = new { url = "http://127.0.0.1:9/hook", headerName = "Authorization" }, secret = "Bearer super-secret-1" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal("***", body.GetProperty("secret").GetString());
        Assert.DoesNotContain("super-secret", await (await admin.GetAsync("/api/notification-channels")).Content.ReadAsStringAsync());

        await using (var db = await Factory().CreateDbContextAsync())
        {
            var stored = await db.NotificationChannels.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.DoesNotContain("super-secret", stored.SecretProtected);
            Assert.Equal("Bearer super-secret-1", _site.Services.GetRequiredService<ISecretProtector>().Unprotect(stored.SecretProtected!));
            Assert.DoesNotContain(await db.OperationLogs.AsNoTracking().Where(x => x.Details != null).Select(x => x.Details!).ToListAsync(), d => d.Contains("super-secret"));
        }

        // *** 表示保持原值；改其他字段后凭据不变。
        var update = await admin.PutAsJsonAsync($"/api/notification-channels/{id}", new { name = body.GetProperty("name").GetString(), type = "Webhook", enabled = false, settings = new { url = "http://127.0.0.1:9/hook2", headerName = "Authorization" }, secret = "***" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var stored = await db.NotificationChannels.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal("Bearer super-secret-1", _site.Services.GetRequiredService<ISecretProtector>().Unprotect(stored.SecretProtected!));
        }
        var cfg = await _site.ClientAsync("cfgA");
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/notification-channels", new { name = "x", type = "Webhook", enabled = true, settings = new { url = "http://a/" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/notification-channels", new { name = "坏地址", type = "Webhook", enabled = true, settings = new { url = "http://u:p@host/" } })).StatusCode);
    }

    [Fact]
    public async Task RoutesAreAreaScopedAndWebhooksAreDeliveredPerAreaAndEscalated()
    {
        await using var receiver = await Receiver.StartAsync();
        var admin = await _site.ClientAsync("admin");
        async Task<int> Channel(string path) => (await (await admin.PostAsJsonAsync("/api/notification-channels", new { name = "hook" + Guid.NewGuid().ToString("N")[..6], type = "Webhook", enabled = true, settings = new { url = receiver.Url + path, headerName = "X-Token" }, secret = "t" + path })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var hookA = await Channel("a"); var hookB = await Channel("b");

        var cfg = await _site.ClientAsync("cfgA");
        object Route(int channel, int? area, int? escalate = null) => new { name = "路由" + Guid.NewGuid().ToString("N")[..4], enabled = true, channelId = channel, areaId = area, minLevel = "Warning", onRaise = escalate is null, onRecover = false, escalateAfterMinutes = escalate };
        Assert.Equal(HttpStatusCode.Created, (await cfg.PostAsJsonAsync("/api/notification-routes", Route(hookA, _site.AreaA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/notification-routes", Route(hookB, _site.AreaB))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/notification-routes", Route(hookB, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/notification-routes", Route(hookB, _site.AreaB))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/notification-routes", Route(hookB, _site.AreaA, escalate: 1))).StatusCode);
        var visible = await cfg.GetFromJsonAsync<JsonElement[]>("/api/notification-routes");
        Assert.All(visible!, x => Assert.Equal(_site.AreaA, x.GetProperty("areaId").GetInt32()));

        var dispatcher = _site.Services.GetRequiredService<AlarmNotificationDispatcher>();
        await using var db = await Factory().CreateDbContextAsync();
        var alarmA = await db.AlarmEvents.FindAsync(await ActiveAlarmAsync(_site.TagA, null));
        var sent = await dispatcher.ProcessAsync(alarmA!, "raise");
        Assert.Single(sent, x => x.Success);
        var hit = Assert.Single(receiver.Hits);
        Assert.Equal(("/a", "ta"), (hit.Path, hit.Token));
        Assert.Contains("活动报警", hit.Body);

        // 未确认超过 1 分钟：A 区升级路由（发往 b 通道）只发一次。
        var old = await db.AlarmEvents.FindAsync(await ActiveAlarmAsync(_site.TagA, null, ScadaTime.NowMs() - 5 * 60_000));
        var escalated = await dispatcher.ScanEscalationsAsync();
        Assert.Contains(escalated, x => x.Action == "escalate" && x.Success);
        Assert.DoesNotContain(await dispatcher.ScanEscalationsAsync(), x => x.Success);
        Assert.Contains(receiver.Hits, x => x.Path == "/b" && x.Body.Contains("escalate"));
        var timeline = await admin.GetFromJsonAsync<JsonElement>($"/api/alarms/{old!.Id}/timeline");
        Assert.Contains(timeline.GetProperty("notifications").EnumerateArray(), x => x.GetProperty("action").GetString() == "escalate");
    }

    [Fact]
    public async Task KpiAndSummaryOnlyCountAuthorizedAreas()
    {
        var start = ScadaTime.NowMs() - 60_000;
        for (var i = 0; i < 3; i++) await ActiveAlarmAsync(_site.TagB, null, start + i);
        await ActiveAlarmAsync(_site.TagA, null, start);
        var op = await _site.ClientAsync("opA"); var admin = await _site.ClientAsync("admin");
        var mine = await op.GetFromJsonAsync<JsonElement>($"/api/alarms/kpi?from={start - 1}&to={start + 10_000}");
        var all = await admin.GetFromJsonAsync<JsonElement>($"/api/alarms/kpi?from={start - 1}&to={start + 10_000}");
        Assert.Equal(1, mine.GetProperty("total").GetInt32());
        Assert.Equal(4, all.GetProperty("total").GetInt32());
        var summary = await op.GetFromJsonAsync<JsonElement>("/api/alarms/summary");
        Assert.True(summary.GetProperty("flood").GetProperty("threshold").GetInt32() > 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/alarms/kpi?from=0&to={ScadaTime.NowMs()}")).StatusCode);
    }

    [Fact]
    public async Task UpgradingFromAreaSchemaKeepsAlarmsAndRules()
    {
        var path = Path.Combine(Path.GetTempPath(), "scada-alarm-migrate-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var old = new ConfigDbContext(options))
            {
                await old.GetService<IMigrator>().MigrateAsync("20261024134241_AddAreasRolesAndAudit");
                await old.Database.ExecuteSqlRawAsync("INSERT INTO Device (Name, Protocol, Host, Port, StationNumber, ByteOrder, PollIntervalMs, TimeoutMs, Enabled, DisabledSinceTs, Parity, Rack, OpcSecurityPolicy, AreaId) VALUES ('旧泵', 0, '10.0.0.5', 502, 1, 0, 1000, 3000, 1, 0, 0, 0, 0, 1)");
                await old.Database.ExecuteSqlRawAsync("INSERT INTO Tag (DeviceId, Kind, Name, Area, Address, DbNumber, BitOffset, DataType, Scale, Offset, Writable, ArchiveMode, Deadband, ArchivePeriodSec) VALUES (1, 0, '压力', 3, 0, 1, 0, 6, 1, 0, 0, 1, 0, 600)");
                await old.Database.ExecuteSqlRawAsync("INSERT INTO AlarmRule (TagId, Type, Threshold, Level, DelaySec, RecoverDeadband, Enabled) VALUES (1, 0, 5, 2, 3, 0.5, 1)");
                await old.Database.ExecuteSqlRawAsync("INSERT INTO AlarmEvent (TagId, RuleId, Level, Value, StartTs, Message) VALUES (1, 1, 2, 6, 100, '旧报警')");
            }
            await using (var db = new ConfigDbContext(options))
            {
                await db.Database.MigrateAsync();
                var rule = await db.AlarmRules.SingleAsync();
                Assert.Equal(3, rule.DelaySec); Assert.Equal(0, rule.OffDelaySec); Assert.False(rule.OutOfService); Assert.Equal(Scada.Core.Enums.AlarmSuppressMode.None, rule.SuppressMode);
                var alarm = await db.AlarmEvents.SingleAsync();
                Assert.Equal("旧报警", alarm.Message); Assert.False(alarm.Shelved); Assert.False(alarm.Suppressed); Assert.Equal(AlarmStates.Unack, AlarmStates.Of(alarm));
                Assert.Empty(await db.NotificationRoutes.ToListAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { }
        }
    }

    private sealed class Receiver : IAsyncDisposable
    {
        private WebApplication? _app;
        public ConcurrentQueue<(string Path, string? Token, string Body)> Hits { get; } = new();
        public string Url { get; private set; } = "";

        public static async Task<Receiver> StartAsync()
        {
            var receiver = new Receiver();
            var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(x => x.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.MapPost("/{name}", async (string name, HttpRequest request) =>
            {
                using var reader = new StreamReader(request.Body);
                receiver.Hits.Enqueue(("/" + name, request.Headers["X-Token"].FirstOrDefault(), await reader.ReadToEndAsync()));
                return Results.Ok();
            });
            await app.StartAsync();
            receiver._app = app;
            receiver.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
            return receiver;
        }

        public async ValueTask DisposeAsync() { if (_app is not null) await _app.DisposeAsync(); }
    }
}
