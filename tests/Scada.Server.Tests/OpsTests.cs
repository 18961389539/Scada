using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.Maintenance;

namespace Scada.Server.Tests;

// 运维：Prometheus 指标鉴权与内容、主备接口在单机下的行为与权限、备份流水线接口与审计、看门狗。
public sealed class OpsTests : IClassFixture<OpsTests.OpsSite>
{
    public sealed class OpsSite : AuthorizationTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder b) => b.UseSetting("Scada:MetricsToken", "metrics-token-0123456789abcdef");
    }
    private readonly OpsSite _site;
    public OpsTests(OpsSite site) => _site = site;

    [Fact]
    public async Task MetricsRequireTokenOrSystemPermissionAndCoverOperationalSignals()
    {
        using var anonymous = _site.Server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/metrics")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await (await _site.ClientAsync("opA")).GetAsync("/metrics")).StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "metrics-token-0123456789abcdef");
        var response = await anonymous.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        foreach (var name in new[] { "scada_up 1", "scada_ha_primary{node=\"standalone\"} 1", "scada_alarms_active{level=\"Warning\"}", "scada_alarms_unacknowledged",
            "scada_notification_outbox_pending", "scada_history_spool_pending_bytes", "scada_disk_free_bytes", "scada_bus_backlog", "scada_backup_last_drill_ok -1",
            "# TYPE scada_device_comm_failures_total counter", "scada_loop_age_seconds{loop=\"collector\"}" })
            Assert.Contains(name, text);
        Assert.Equal(HttpStatusCode.OK, (await (await _site.ClientAsync("admin")).GetAsync("/metrics")).StatusCode);
    }

    [Fact]
    public async Task HaEndpointsInStandaloneModeArePermissionedAndAudited()
    {
        using var anonymous = _site.Server.CreateClient();
        var role = await anonymous.GetFromJsonAsync<JsonElement>("/api/ha/role");
        Assert.False(role.GetProperty("enabled").GetBoolean()); Assert.Equal("Standalone", role.GetProperty("role").GetString());
        var op = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/ha/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/ha/switchover", new { reason = "x" })).StatusCode);
        var admin = await _site.ClientAsync("admin");
        var status = await admin.GetFromJsonAsync<JsonElement>("/api/ha/status");
        Assert.False(status.GetProperty("enabled").GetBoolean());
        var refused = await admin.PostAsJsonAsync("/api/ha/switchover", new { reason = "演练" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.NotNull(await _site.LastLogAsync(x => x.Action == "主备手动切换" && x.Result.Contains("未启用主备")));
        // 节点间接口：未启用或密钥不对一律 401。
        anonymous.DefaultRequestHeaders.Add("X-Scada-Ha-Key", "whatever-key-whatever-key-whatever-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/ha/peer/manifest")).StatusCode);
    }

    [Fact]
    public async Task BackupRunEndpointDrillsAndAudits()
    {
        var admin = await _site.ClientAsync("admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _site.ClientAsync("opA")).PostAsync("/api/maintenance/backups/run", null)).StatusCode);
        var run = await (await admin.PostAsync("/api/maintenance/backups/run", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(run.GetProperty("ok").GetBoolean(), run.GetRawText());
        Assert.True(run.GetProperty("drill").GetProperty("ok").GetBoolean());
        var file = run.GetProperty("fileName").GetString()!;
        Assert.NotNull(await _site.LastLogAsync(x => x.Action == "备份并演练" && x.TargetId == file));
        var runs = await admin.GetFromJsonAsync<JsonElement[]>("/api/maintenance/backups/runs");
        Assert.Equal(file, runs![0].GetProperty("fileName").GetString());
        var drill = await (await admin.PostAsync($"/api/maintenance/backups/{file}/drill", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(drill.GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/maintenance/backups/..%2Fx.zip/drill", null)).StatusCode);
        Assert.Empty(await admin.GetFromJsonAsync<JsonElement[]>("/api/maintenance/backups/targets") ?? []);
    }

    [Fact]
    public async Task WatchdogStopsTheHostWhenALoopStalls()
    {
        var liveness = new LivenessRegistry();
        liveness.Beat("collector");
        var lifetime = new Lifetime();
        var watchdog = new WatchdogService(liveness, Options.Create(new ScadaSettings { WatchdogStallSeconds = 1 }), lifetime, NullLogger<WatchdogService>.Instance) { CheckInterval = TimeSpan.FromMilliseconds(100) };
        var previous = Environment.ExitCode;
        try
        {
            await watchdog.StartAsync(default);
            await Task.Delay(500); Assert.False(lifetime.Stopped); // 1 秒内仍在阈值内
            await Task.Delay(1500);
            Assert.True(lifetime.Stopped);
            Assert.Equal(WatchdogService.StallExitCode, Environment.ExitCode);
        }
        finally { Environment.ExitCode = previous; await watchdog.StopAsync(default); }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public bool Stopped;
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => Stopped = true;
    }
}

// 节点为备机时：控制检查与下发被拒绝（采集与定时任务的门控由主备集成测试覆盖）。
public sealed class StandbyGateTests : IClassFixture<StandbyGateTests.StandbySite>
{
    public sealed class ToggleRole : INodeRole
    {
        public volatile bool Active = true;
        public bool IsActive => Active;
        public long Generation => Active ? 1 : 2;
    }
    public sealed class StandbySite : ControlTests.ControlSite
    {
        public readonly ToggleRole Role = new();
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.RemoveAll<INodeRole>(); services.AddSingleton<INodeRole>(Role);
        }
    }
    private readonly StandbySite _site;
    public StandbyGateTests(StandbySite site) => _site = site;

    [Fact]
    public async Task StandbyRefusesControlChecksAndWrites()
    {
        var admin = await _site.ClientAsync("admin");
        var tag = (await (await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "备机门控" + Guid.NewGuid().ToString("N")[..4]))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var op = await _site.ClientAsync("opA");
        _site.Role.Active = false;
        try
        {
            var check = await (await op.PostAsJsonAsync("/api/control/check", new { tagId = tag, value = 1 })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(check.GetProperty("refusals").EnumerateArray(), x => x.GetString()!.Contains("备机"));
            var write = await (await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(write.GetProperty("success").GetBoolean()); Assert.Contains("备机", write.GetProperty("message").GetString());
            Assert.Equal(0, _site.Services.GetRequiredService<ControlTests.FakeCollector>().Count(tag));
        }
        finally { _site.Role.Active = true; }
        var ok = await (await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(ok.GetProperty("success").GetBoolean(), ok.GetRawText());
    }
}
