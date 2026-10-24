using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;

namespace Scada.Server.Tests;

// 端到端（进程内）验证区域授权：HTTP 读写、控制、报警确认、SignalR 推送、审计差分。
public sealed class AuthorizationTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly Site _site;
    public AuthorizationTests(Site site) => _site = site;

    [Fact]
    public async Task AreaScopedUserOnlySeesOwnAreaDevicesTagsAndRealtime()
    {
        var c = await _site.ClientAsync("opA");
        var devices = await c.GetFromJsonAsync<JsonElement[]>("/api/devices");
        Assert.Equal(new[] { _site.DeviceA }, devices!.Select(x => x.GetProperty("id").GetInt32()));
        var tags = await c.GetFromJsonAsync<JsonElement[]>("/api/tags");
        var tagIds = tags!.Select(x => x.GetProperty("id").GetInt32()).ToArray();
        Assert.Contains(_site.TagA, tagIds);
        Assert.DoesNotContain(_site.TagB, tagIds);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/devices/{_site.DeviceB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/tags/{_site.TagB}")).StatusCode);

        var cache = _site.Services.GetRequiredService<IRealtimeCache>();
        cache.Set(new TagValue(_site.TagA, 1, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));
        cache.Set(new TagValue(_site.TagB, 2, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));
        var values = await c.GetFromJsonAsync<JsonElement[]>($"/api/realtime?tagIds={_site.TagA},{_site.TagB}");
        Assert.All(values!, v => Assert.Equal(_site.TagA, v.GetProperty("tagId").GetInt32()));
        var all = await c.GetFromJsonAsync<JsonElement[]>("/api/realtime");
        Assert.DoesNotContain(all!, v => v.GetProperty("tagId").GetInt32() == _site.TagB);
    }

    [Fact]
    public async Task CrossAreaHistoryReportAndExportAreRejected()
    {
        var c = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/history?tagIds={_site.TagB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/history?tagIds={_site.TagA},{_site.TagB}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/history?tagIds={_site.TagA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/reports?tagIds={_site.TagB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/api/reports/export?tagIds={_site.TagB}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/reports/export?tagIds={_site.TagA}")).StatusCode);
    }

    [Fact]
    public async Task CrossAreaControlIsRejectedAndAudited()
    {
        var c = await _site.ClientAsync("opA");
        var response = await c.PostAsJsonAsync("/api/commands", new { tagId = _site.TagB, value = 1, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var log = await _site.LastLogAsync(x => x.UserName == "opA" && x.Action.Contains($"#{_site.TagB}"));
        Assert.NotNull(log);
        Assert.StartsWith("拒绝", log!.Result);
        // 本区域测点可以进入控制流程（设备离线，命令会失败，但不是 403）。
        var own = await c.PostAsJsonAsync("/api/commands", new { tagId = _site.TagA, value = 1, requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task ViewerRoleCannotWriteConfigurationAndDenialIsAudited()
    {
        var c = await _site.ClientAsync("opA");
        var response = await c.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "越权新增"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var log = await _site.LastLogAsync(x => x.UserName == "opA" && x.Action == "POST /api/tags");
        Assert.NotNull(log);
        Assert.StartsWith("拒绝", log!.Result);
    }

    [Fact]
    public async Task CustomConfigureRoleIsLimitedToItsArea()
    {
        var c = await _site.ClientAsync("cfgA");
        var ok = await c.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "A区新增" + Guid.NewGuid().ToString("N")[..6]));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var denied = await c.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceB, "B区新增"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        // 只有配置权、没有控制/确认权。
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/commands", new { tagId = _site.TagA, value = 1, requestId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/alarms/{_site.AlarmA}/ack", null)).StatusCode);
        // 不能把本区设备移到无权区域，也不能管理用户。
        var device = await c.GetFromJsonAsync<JsonElement>($"/api/devices/{_site.DeviceA}");
        var move = await c.PutAsJsonAsync($"/api/devices/{_site.DeviceA}", _site.DeviceBody(device, _site.AreaB));
        Assert.Equal(HttpStatusCode.Forbidden, move.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/logs")).StatusCode);
    }

    [Fact]
    public async Task AlarmListAndAckFollowAreas()
    {
        var c = await _site.ClientAsync("opA");
        var alarms = await c.GetFromJsonAsync<JsonElement[]>("/api/alarms?activeOnly=true");
        var ids = alarms!.Select(x => x.GetProperty("id").GetInt32()).ToArray();
        Assert.Contains(_site.AlarmA, ids);
        Assert.DoesNotContain(_site.AlarmB, ids);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync($"/api/alarms/{_site.AlarmB}/ack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/alarms/{_site.AlarmA}/ack", null)).StatusCode);
    }

    [Fact]
    public async Task CalculatedTagCannotReadAcrossAreas()
    {
        var c = await _site.ClientAsync("cfgA");
        var response = await c.PostAsJsonAsync("/api/calculated-tags", new { name = "偷看B" + Guid.NewGuid().ToString("N")[..6], expression = $"#{_site.TagB} * 2" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var own = await c.PostAsJsonAsync("/api/calculated-tags", new { name = "A计算" + Guid.NewGuid().ToString("N")[..6], expression = $"#{_site.TagA} * 2" });
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [Fact]
    public async Task AdminDeviceUpdateWritesFieldDiff()
    {
        var c = await _site.ClientAsync("admin");
        var device = await c.GetFromJsonAsync<JsonElement>($"/api/devices/{_site.DeviceB}");
        var body = _site.DeviceBody(device, null, name: "B区设备-改名", pollIntervalMs: 2500);
        var response = await c.PutAsJsonAsync($"/api/devices/{_site.DeviceB}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var log = await _site.LastLogAsync(x => x.TargetType == "Device" && x.TargetId == _site.DeviceB.ToString() && x.Action.StartsWith("修改设备"));
        Assert.NotNull(log);
        using var details = JsonDocument.Parse(log!.Details!);
        Assert.Equal("update", details.RootElement.GetProperty("op").GetString());
        var changes = details.RootElement.GetProperty("changes");
        Assert.Equal("B区设备", changes.GetProperty("name").GetProperty("old").GetString());
        Assert.Equal("B区设备-改名", changes.GetProperty("name").GetProperty("new").GetString());
        Assert.Equal(1000, changes.GetProperty("pollIntervalMs").GetProperty("old").GetInt32());
        Assert.False(changes.TryGetProperty("host", out _));
        // 改回，避免影响其他用例。
        await c.PutAsJsonAsync($"/api/devices/{_site.DeviceB}", _site.DeviceBody(device, null));
    }

    [Fact]
    public async Task RoleAndAssignmentChangesAreAuditedWithDiff()
    {
        var c = await _site.ClientAsync("admin");
        var role = await (await c.PostAsJsonAsync("/api/roles", new { name = "临时角色" + Guid.NewGuid().ToString("N")[..6], permissions = new[] { "View" } })).Content.ReadFromJsonAsync<JsonElement>();
        var roleId = role.GetProperty("id").GetInt32();
        var put = await c.PutAsJsonAsync($"/api/roles/{roleId}", new { name = role.GetProperty("name").GetString(), permissions = new[] { "View", "Control" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var log = await _site.LastLogAsync(x => x.TargetType == "Role" && x.TargetId == roleId.ToString() && x.Action.StartsWith("修改角色"));
        Assert.Contains("Control", log!.Details);
        var bad = await c.PostAsJsonAsync("/api/roles", new { name = "x", permissions = new[] { "Fly" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var failed = await _site.LastLogAsync(x => x.Action == "POST /api/roles");
        Assert.StartsWith("失败", failed!.Result);
    }

    [Fact]
    public async Task SignalRSubscriptionOnlyReceivesAuthorizedTagsAndStopsAfterRevocation()
    {
        var token = await _site.TokenAsync("watchA");
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_site.Server.BaseAddress, "/hubs/realtime"), o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.HttpMessageHandlerFactory = _ => _site.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            }).Build();
        var received = new List<int>();
        var alarms = new List<int>();
        connection.On<JsonElement[]>("values", list => { lock (received) received.AddRange(list.Select(x => x.GetProperty("tagId").GetInt32())); });
        connection.On<JsonElement>("alarm", a => { lock (alarms) alarms.Add(a.GetProperty("alarm").GetProperty("tagId").GetInt32()); });
        await connection.StartAsync();
        var effective = await connection.InvokeAsync<int[]>("Subscribe", new[] { _site.TagA, _site.TagB });
        Assert.Equal(new[] { _site.TagA }, effective);

        var bus = _site.Services.GetRequiredService<IRealtimeBus>();
        var notifier = _site.Services.GetRequiredService<IAlarmNotifier>();
        for (var i = 0; i < 20 && received.Count == 0; i++)
        {
            bus.Publish(new TagValue(_site.TagA, i, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));
            bus.Publish(new TagValue(_site.TagB, i, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));
            await Task.Delay(200);
        }

        await notifier.PublishAlarmAsync(new AlarmEvent { Id = 900001, TagId = _site.TagB, Message = "B" }, "raise");
        await notifier.PublishAlarmAsync(new AlarmEvent { Id = 900002, TagId = _site.TagA, Message = "A" }, "raise");
        await Task.Delay(1500);
        lock (received) Assert.Contains(_site.TagA, received);
        lock (received) Assert.DoesNotContain(_site.TagB, received);
        lock (alarms) Assert.Equal(new[] { _site.TagA }, alarms);

        // 撤销授权后，已有订阅不再收到推送（无需重连）。
        var admin = await _site.ClientAsync("admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/users/{_site.WatchUserId}/assignments", new { assignments = Array.Empty<object>() })).StatusCode);
        await Task.Delay(300);
        lock (received) received.Clear();
        for (var i = 0; i < 5; i++)
        {
            bus.Publish(new TagValue(_site.TagA, 100 + i, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));
            await Task.Delay(200);
        }

        await Task.Delay(800);
        lock (received) Assert.Empty(received);
    }

    public class Site : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "scada-authz-" + Guid.NewGuid().ToString("N"));
        private WebApplicationFactory<Program>? _factory;
        private readonly Dictionary<string, string> _tokens = new();
        public Microsoft.AspNetCore.TestHost.TestServer Server => _factory!.Server;
        public IServiceProvider Services => _factory!.Services;
        public int AreaA, AreaB, DeviceA, DeviceB, TagA, TagB, AlarmA, AlarmB, WatchUserId;

        protected virtual void ConfigureServices(IServiceCollection services) { }

        public virtual async Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("Scada:ConfigDbPath", Path.Combine(_root, "config.db"));
                b.UseSetting("Scada:HistoryRoot", Path.Combine(_root, "history"));
                b.UseSetting("Scada:BackupRoot", Path.Combine(_root, "backups"));
                b.UseSetting("Scada:DiagnosticsRoot", Path.Combine(_root, "diag"));
                b.UseSetting("Scada:ReportsRoot", Path.Combine(_root, "reports"));
                b.UseSetting("Scada:ReportSchedulerEnabled", "false");
                b.UseSetting("Scada:SeedDemoData", "false");
                b.UseSetting("Scada:FullBackupIntervalHours", "0");
                b.UseSetting("Scada:PushIntervalMs", "100");
                b.UseSetting("Jwt:Key", "authorization-tests-signing-key-0123456789abcdef");
                b.ConfigureTestServices(ConfigureServices);
            });
            _ = _factory.Server;
            var admin = await ClientAsync("admin", "admin123");
            AreaA = await IdAsync(await admin.PostAsJsonAsync("/api/areas", new { name = "A车间" }));
            AreaB = await IdAsync(await admin.PostAsJsonAsync("/api/areas", new { name = "B车间" }));
            DeviceA = await IdAsync(await admin.PostAsJsonAsync("/api/devices", DeviceRequest("A区设备", AreaA)));
            DeviceB = await IdAsync(await admin.PostAsJsonAsync("/api/devices", DeviceRequest("B区设备", AreaB)));
            TagA = await IdAsync(await admin.PostAsJsonAsync("/api/tags", TagBody(DeviceA, "A温度")));
            TagB = await IdAsync(await admin.PostAsJsonAsync("/api/tags", TagBody(DeviceB, "B温度")));
            var operate = await IdAsync(await admin.PostAsJsonAsync("/api/roles", new { name = "区域操作员", permissions = new[] { "View", "Control", "AckAlarm" } }));
            var configure = await IdAsync(await admin.PostAsJsonAsync("/api/roles", new { name = "区域配置员", permissions = new[] { "View", "Configure" } }));
            var opA = await CreateUserAsync(admin, "opA");
            var cfgA = await CreateUserAsync(admin, "cfgA");
            WatchUserId = await CreateUserAsync(admin, "watchA");
            await Ok(await admin.PutAsJsonAsync($"/api/users/{opA}/assignments", new { assignments = new[] { new { roleId = operate, areaId = AreaA } } }));
            await Ok(await admin.PutAsJsonAsync($"/api/users/{cfgA}/assignments", new { assignments = new[] { new { roleId = configure, areaId = AreaA } } }));
            await Ok(await admin.PutAsJsonAsync($"/api/users/{WatchUserId}/assignments", new { assignments = new[] { new { roleId = operate, areaId = AreaA } } }));

            var factory = Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var a = new AlarmEvent { TagId = TagA, DeviceId = DeviceA, Message = "A高温", StartTs = ScadaTime.NowMs() };
            var bAlarm = new AlarmEvent { TagId = TagB, DeviceId = DeviceB, Message = "B高温", StartTs = ScadaTime.NowMs() };
            db.AlarmEvents.AddRange(a, bAlarm);
            await db.SaveChangesAsync();
            AlarmA = a.Id; AlarmB = bAlarm.Id;
        }

        private static async Task Ok(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        private static async Task<int> CreateUserAsync(HttpClient admin, string name) =>
            await IdAsync(await admin.PostAsJsonAsync("/api/users", new { userName = name, password = "secret123", displayName = name, role = "None", enabled = true }));

        private static async Task<int> IdAsync(HttpResponseMessage response)
        {
            await Ok(response);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }

        public static object DeviceRequest(string name, int area) => new
        {
            name, protocol = "ModbusTcp", host = "127.0.0.1", port = 1502, stationNumber = 1, byteOrder = "ABCD", pollIntervalMs = 1000, timeoutMs = 500, enabled = false, areaId = area
        };

        public object TagBody(int deviceId, string name) => new
        {
            deviceId, name, area = "HoldingRegister", address = 0, dataType = "Float", scale = 1.0, offset = 0.0, writable = true, archiveMode = "None", deadband = 0.0, archivePeriodSec = 600
        };

        public object DeviceBody(JsonElement device, int? area, string? name = null, int? pollIntervalMs = null) => new
        {
            name = name ?? device.GetProperty("name").GetString(), protocol = device.GetProperty("protocol").GetString(), host = device.GetProperty("host").GetString(),
            port = device.GetProperty("port").GetInt32(), stationNumber = device.GetProperty("stationNumber").GetInt32(), byteOrder = device.GetProperty("byteOrder").GetString(),
            pollIntervalMs = pollIntervalMs ?? device.GetProperty("pollIntervalMs").GetInt32(), timeoutMs = device.GetProperty("timeoutMs").GetInt32(), enabled = false, areaId = area
        };

        public async Task<string> TokenAsync(string user, string password = "secret123")
        {
            if (_tokens.TryGetValue(user, out var cached)) return cached;
            using var anonymous = _factory!.CreateClient();
            var response = await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = user, password });
            await Ok(response);
            var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            _tokens[user] = token;
            return token;
        }

        public async Task<HttpClient> ClientAsync(string user, string? password = null)
        {
            var token = await TokenAsync(user, password ?? (user == "admin" ? "admin123" : "secret123"));
            var client = _factory!.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        public async Task<OperationLog?> LastLogAsync(Func<OperationLog, bool> predicate)
        {
            var factory = Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            return (await db.OperationLogs.AsNoTracking().OrderByDescending(x => x.Id).Take(500).ToListAsync()).FirstOrDefault(predicate);
        }

        public virtual async Task DisposeAsync()
        {
            if (_factory is not null) await _factory.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
