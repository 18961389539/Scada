using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;
using static Scada.Server.Tests.McpApiTests;

namespace Scada.Server.Tests;

// MCP 写操作：报警处置、测点写入、顺控、配方、控制权/挂牌与采集重连。
// 全部以 mcp:lan 执行、走现有控制闭环并留审计；双人复核对象在 MCP 侧直接拒绝。
public sealed class McpWriteTests : IClassFixture<McpWriteTests.Site>
{
    public sealed class FakeDiagnostics : ICollectorDiagnostics
    {
        public int? SnapshotDeviceId;
        public readonly List<int> Reconnects = [];
        public CollectorDeviceDiagnostics? GetDiagnostics(int deviceId) =>
            deviceId == SnapshotDeviceId ? Snapshot(deviceId) : null;
        public bool RequestReconnect(int deviceId) { lock (Reconnects) Reconnects.Add(deviceId); return true; }
        private static CollectorDeviceDiagnostics Snapshot(int deviceId) => new(
            deviceId, "MCP fake", "ModbusTcp", "", "Running", "Poll", null, null, true, false, 1,
            ScadaTime.NowMs(), ScadaTime.NowMs(), 1000, 1000, 5000, 0, 1,
            new CollectorQualityCounts(1, 0, 0, 0, 0, 0),
            new CollectorDiagnosticMetrics(0, 0, 0, 0, 0, 0, 0, null, null, null, null, null, 0, 0, 0, 0, null, 0, 0, 0, 0, 0),
            [], [], [], []);
    }

    public sealed class Site : ControlTests.ControlSite
    {
        public readonly FakeDiagnostics Diagnostics = new();
        public int RuleId, SequenceId, DualSequenceId, RecipeId, DualRecipeId, ValveTag, RecipeTag, DualTag, GatewayDevice, NoSnapshotDevice;

        protected override void ConfigureSettings(IWebHostBuilder builder) =>
            builder.UseSetting("Mcp:RequestsPerMinute", "1000");

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.RemoveAll<ICollectorDiagnostics>();
            services.AddSingleton(Diagnostics);
            services.AddSingleton<ICollectorDiagnostics>(sp => sp.GetRequiredService<FakeDiagnostics>());
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            Diagnostics.SnapshotDeviceId = DeviceA;
            var admin = await ClientAsync("admin");
            var factory = Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var rule = new AlarmRule
                {
                    TagId = TagA, Type = AlarmRuleType.High, Threshold = 50, Level = AlarmLevel.Warning,
                    Enabled = true, Message = "MCP write alarm"
                };
                var gateway = new AcquisitionGateway { Name = "MCP write gateway", TokenHash = "secret-token-hash" };
                db.AlarmRules.Add(rule);
                db.Gateways.Add(gateway);
                await db.SaveChangesAsync();
                RuleId = rule.Id;
                var remote = await db.Devices.SingleAsync(x => x.Id == DeviceB);
                remote.GatewayId = gateway.Id;
                await db.SaveChangesAsync();
                GatewayDevice = remote.Id;
            }
            ValveTag = await TagAsync(admin, "MCP顺控阀");
            RecipeTag = await TagAsync(admin, "MCP配方向");
            DualTag = await TagAsync(admin, "MCP复核阀");
            NoSnapshotDevice = await IdAsync(await admin.PostAsJsonAsync("/api/devices",
                AuthorizationTests.Site.DeviceRequest("MCP无快照设备", AreaA)));

            SequenceId = await SequenceAsync(admin, "MCP顺控", false);
            DualSequenceId = await SequenceAsync(admin, "MCP复核顺控", true);
            RecipeId = await RecipeAsync(admin, false);
            DualRecipeId = await RecipeAsync(admin, true);
        }

        private async Task<int> TagAsync(HttpClient admin, string name) =>
            (await Ok(await admin.PostAsJsonAsync("/api/tags", TagBody(DeviceA, name + Guid.NewGuid().ToString("N")[..5]))))
            .GetProperty("id").GetInt32();

        private async Task<int> SequenceAsync(HttpClient admin, string name, bool dual) =>
            (await Ok(await admin.PostAsJsonAsync("/api/control/sequences", new
            {
                name = name + Guid.NewGuid().ToString("N")[..5], areaId = AreaA, dualApproval = dual,
                steps = new[] { new { name = "开阀", tagId = ValveTag, value = 25.0, confirmExpression = (string?)null, timeoutMs = 500, delayMs = 0 } }
            }))).GetProperty("id").GetInt32();

        private async Task<int> RecipeAsync(HttpClient admin, bool dual)
        {
            var template = (await Ok(await admin.PostAsJsonAsync("/api/recipes/templates", new
            {
                name = "MCP配方" + Guid.NewGuid().ToString("N")[..5], areaId = AreaA, dualApproval = dual,
                parameters = new object[] { new { key = "flow", name = "流量", tagId = RecipeTag, unit = "m3/h", min = 0, max = 100 } }
            }))).GetProperty("id").GetInt32();
            var recipe = await Ok(await admin.PostAsJsonAsync("/api/recipes", new
            {
                templateId = template, name = "产品" + Guid.NewGuid().ToString("N")[..5],
                values = new[] { new { key = "flow", value = 40.0 } }, note = "MCP"
            }));
            return recipe.GetProperty("recipe").GetProperty("id").GetInt32();
        }

        private static async Task<JsonElement> Ok(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        private async Task<int> IdAsync(HttpResponseMessage response) =>
            (await Ok(response)).GetProperty("id").GetInt32();
    }

    private readonly Site _site;
    public McpWriteTests(Site site) => _site = site;
    private ControlTests.FakeCollector Collector => _site.Services.GetRequiredService<ControlTests.FakeCollector>();
    private IDbContextFactory<ConfigDbContext> Db => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private void Set(int tagId, double value) =>
        _site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(tagId, value, TagQuality.Good, ScadaTime.NowMs()));

    private static async Task<JsonElement> Call(HttpClient client, string name, object? arguments = null) =>
        McpApiTests.Data(await CallAsync(client, name, arguments));

    private static async Task<JsonElement> Error(HttpClient client, string name, object? arguments = null)
    {
        var result = await CallAsync(client, name, arguments);
        Assert.True(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), result.ToString());
        return result.GetProperty("structuredContent").GetProperty("error");
    }

    [Fact]
    public async Task Alarm_Tools_Acknowledge_Annotate_Shelve_With_Audit()
    {
        using var client = _site.Server.CreateClient();
        var ack = await Call(client, "acknowledge_alarm", new { alarmId = _site.AlarmA, note = "MCP 已确认" });
        Assert.True(ack.GetProperty("success").GetBoolean(), ack.ToString());
        Assert.Equal("mcp:lan", ack.GetProperty("ackUser").GetString());
        var firstAckTs = ack.GetProperty("ackTs").GetInt64();

        var again = await Call(client, "acknowledge_alarm", new { alarmId = _site.AlarmA, note = "MCP 补充" });
        Assert.True(again.GetProperty("success").GetBoolean());
        Assert.Equal(firstAckTs, again.GetProperty("ackTs").GetInt64());

        var note = await Call(client, "annotate_alarm", new { alarmId = _site.AlarmB, note = "MCP 备注" });
        Assert.True(note.GetProperty("activityId").GetInt64() > 0);

        var shelved = await Call(client, "shelve_alarm_rule", new { ruleId = _site.RuleId, minutes = 5, reason = "MCP 测试暂挂" });
        Assert.True(shelved.GetProperty("success").GetBoolean(), shelved.ToString());
        Assert.True(shelved.GetProperty("untilTs").GetInt64() > shelved.GetProperty("startTs").GetInt64());
        var unshelved = await Call(client, "unshelve_alarm_rule", new { ruleId = _site.RuleId });
        Assert.True(unshelved.GetProperty("success").GetBoolean());
        var second = await Call(client, "unshelve_alarm_rule", new { ruleId = _site.RuleId });
        Assert.False(second.GetProperty("success").GetBoolean());

        Assert.Equal("not_found", (await Error(client, "acknowledge_alarm", new { alarmId = 999999 })).GetProperty("code").GetString());
        Assert.Equal("not_found", (await Error(client, "shelve_alarm_rule", new { ruleId = 999999, minutes = 5, reason = "x" })).GetProperty("code").GetString());
        Assert.Equal("invalid_argument", (await Error(client, "shelve_alarm_rule", new { ruleId = _site.RuleId, minutes = 100000, reason = "x" })).GetProperty("code").GetString());

        await using var db = await Db.CreateDbContextAsync();
        var alarm = await db.AlarmEvents.AsNoTracking().SingleAsync(x => x.Id == _site.AlarmA);
        Assert.Equal("mcp:lan", alarm.AckUser);
        Assert.Equal(firstAckTs, alarm.AckTs);
        Assert.Contains(await db.AlarmActivities.AsNoTracking().Where(x => x.AlarmEventId == _site.AlarmA).ToListAsync(),
            x => x.User == "mcp:lan" && x.Action == "ack");
        Assert.True(await db.AlarmShelves.AsNoTracking().AnyAsync(x => x.RuleId == _site.RuleId && x.ClearedTs != null));

        var log = await _site.LastLogAsync(x => x.TargetType == "McpTool" && x.Action == "MCP acknowledge_alarm" && x.Result == "成功");
        Assert.NotNull(log);
        await using var audit = await Db.CreateDbContextAsync();
        Assert.True(await audit.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "McpTool" && x.Action == "MCP unshelve_alarm_rule" && x.Result == "失败：操作未成功"));
    }

    [Fact]
    public async Task Write_Tools_Run_Through_The_Control_Pipeline()
    {
        using var client = _site.Server.CreateClient();
        Set(_site.TagA, 10);
        var check = await Call(client, "check_tag_write", new { tagId = _site.TagA, value = 30 });
        Assert.Empty(check.GetProperty("refusals").EnumerateArray());
        Assert.Equal("Remote", check.GetProperty("mode").GetString());

        var write = await Call(client, "write_tag_value", new { tagId = _site.TagA, value = 30, requestId = Guid.NewGuid() });
        Assert.True(write.GetProperty("success").GetBoolean(), write.ToString());
        Assert.Equal(30, write.GetProperty("readBack").GetDouble());
        Assert.Equal(1, Collector.Count(_site.TagA));
        var controlLog = await _site.LastLogAsync(x => x.TagId == _site.TagA && x.Action.StartsWith("控制写入"));
        Assert.NotNull(controlLog);
        Assert.Equal("mcp:lan", controlLog.UserName);

        var local = await Call(client, "set_device_control_mode", new { deviceId = _site.DeviceA, mode = "Local", reason = "MCP 就地测试" });
        Assert.True(local.GetProperty("success").GetBoolean(), local.ToString());
        Assert.Equal("Remote", local.GetProperty("previousMode").GetString());
        var blocked = await Call(client, "write_tag_value", new { tagId = _site.TagA, value = 35 });
        Assert.False(blocked.GetProperty("success").GetBoolean());
        Assert.Contains("就地控制", blocked.GetProperty("message").GetString());
        Assert.Equal(1, Collector.Count(_site.TagA));
        await using (var audit = await Db.CreateDbContextAsync())
            Assert.True(await audit.OperationLogs.AsNoTracking().AnyAsync(
                x => x.TargetType == "McpTool" && x.Action == "MCP write_tag_value" && x.Result == "失败：操作未成功"));
        Assert.True((await Call(client, "set_device_control_mode", new { deviceId = _site.DeviceA, mode = "Remote", reason = "测试结束" }))
            .GetProperty("success").GetBoolean());

        var lockout = await Call(client, "place_control_lockout", new { tagId = _site.TagA, kind = "NoOperate", reason = "MCP 挂牌" });
        Assert.True(lockout.GetProperty("success").GetBoolean(), lockout.ToString());
        Assert.Contains("禁止操作", (await Call(client, "write_tag_value", new { tagId = _site.TagA, value = 35 }))
            .GetProperty("message").GetString());
        var maintenance = await Call(client, "place_control_lockout", new { tagId = _site.TagA, kind = "Maintenance", reason = "x" });
        Assert.False(maintenance.GetProperty("success").GetBoolean());
        Assert.True((await Call(client, "remove_control_lockout", new { lockoutId = lockout.GetProperty("lockoutId").GetInt32(), reason = "MCP 摘牌" }))
            .GetProperty("success").GetBoolean());
        Assert.False((await Call(client, "remove_control_lockout", new { lockoutId = lockout.GetProperty("lockoutId").GetInt32(), reason = "再次摘牌" }))
            .GetProperty("success").GetBoolean());
        Assert.True((await Call(client, "write_tag_value", new { tagId = _site.TagA, value = 35 })).GetProperty("success").GetBoolean());

        var admin = await _site.ClientAsync("admin");
        Assert.True((await admin.PutAsJsonAsync($"/api/control/points/{_site.DualTag}",
            new { requireConfirm = false, dualApproval = true, approvalTimeoutSec = 300, feedbackTimeoutMs = 10000 })).IsSuccessStatusCode);
        var dualCheck = await Call(client, "check_tag_write", new { tagId = _site.DualTag, value = 1 });
        Assert.True(dualCheck.GetProperty("dualApproval").GetBoolean());
        var dual = await Call(client, "write_tag_value", new { tagId = _site.DualTag, value = 1 });
        Assert.False(dual.GetProperty("success").GetBoolean());
        Assert.Contains("双人复核", dual.GetProperty("message").GetString());

        Assert.Equal("not_found", (await Error(client, "write_tag_value", new { tagId = 999999, value = 1 })).GetProperty("code").GetString());
        Assert.Equal("not_found", (await Error(client, "check_tag_write", new { tagId = 999999, value = 1 })).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Sequence_And_Recipe_Tools_Execute_Or_Refuse_Dual_Approval()
    {
        using var client = _site.Server.CreateClient();
        Set(_site.ValveTag, 0);
        Set(_site.RecipeTag, 1);
        var workflow = _site.Services.GetRequiredService<ControlWorkflowService>();

        var started = await Call(client, "start_control_sequence", new { sequenceId = _site.SequenceId });
        Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
        var runId = started.GetProperty("runId").GetInt32();
        await workflow.WaitAsync(runId);
        var admin = await _site.ClientAsync("admin");
        using (var run = await admin.GetAsync($"/api/control/runs/{runId}"))
        {
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            var body = await run.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Completed", body.GetProperty("status").GetString());
        }
        Assert.Equal(1, Collector.Count(_site.ValveTag));

        var dualSequence = await Call(client, "start_control_sequence", new { sequenceId = _site.DualSequenceId });
        Assert.False(dualSequence.GetProperty("success").GetBoolean());
        Assert.Contains("Dual-approval", dualSequence.GetProperty("message").GetString());
        Assert.Equal("not_found", (await Error(client, "start_control_sequence", new { sequenceId = 999999 })).GetProperty("code").GetString());
        var aborted = await Call(client, "abort_control_sequence", new { runId });
        Assert.False(aborted.GetProperty("success").GetBoolean());

        var recipeCheck = await Call(client, "check_recipe_download", new { recipeId = _site.RecipeId });
        Assert.True(recipeCheck.GetProperty("success").GetBoolean(), recipeCheck.ToString());
        Assert.False(recipeCheck.GetProperty("dualApproval").GetBoolean());
        var parameter = Assert.Single(recipeCheck.GetProperty("parameters").EnumerateArray());
        Assert.Equal(40, parameter.GetProperty("target").GetDouble());
        Assert.Empty(parameter.GetProperty("refusals").EnumerateArray());

        var download = await Call(client, "apply_recipe", new { recipeId = _site.RecipeId });
        Assert.True(download.GetProperty("success").GetBoolean(), download.ToString());
        var downloadId = download.GetProperty("downloadId").GetInt32();
        await _site.Services.GetRequiredService<RecipeService>().WaitAsync(downloadId);
        using (var result = await admin.GetAsync($"/api/recipes/downloads/{downloadId}"))
        {
            var body = await result.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Completed", body.GetProperty("status").GetString());
            Assert.Equal(1, body.GetProperty("written").GetInt32());
        }
        Assert.Equal(1, Collector.Count(_site.RecipeTag));

        var dualRecipe = await Call(client, "apply_recipe", new { recipeId = _site.DualRecipeId });
        Assert.False(dualRecipe.GetProperty("success").GetBoolean());
        Assert.Contains("Dual-approval", dualRecipe.GetProperty("message").GetString());
        Assert.Equal("not_found", (await Error(client, "check_recipe_download", new { recipeId = 999999 })).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Device_Tools_Switch_Mode_Reconnect_And_Reject_Remote_Gateway()
    {
        using var client = _site.Server.CreateClient();
        var local = await Call(client, "set_device_control_mode", new { deviceId = _site.DeviceA, mode = "Local", reason = "MCP 就地" });
        Assert.True(local.GetProperty("success").GetBoolean(), local.ToString());
        await using (var db = await Db.CreateDbContextAsync())
            Assert.Equal(DeviceControlMode.Local, (await db.DeviceControlStates.AsNoTracking().SingleAsync(x => x.DeviceId == _site.DeviceA)).Mode);
        Assert.True((await Call(client, "set_device_control_mode", new { deviceId = _site.DeviceA, mode = "Remote", reason = "恢复远程" }))
            .GetProperty("success").GetBoolean());
        Assert.Equal("not_found", (await Error(client, "set_device_control_mode", new { deviceId = 999999, mode = "Local", reason = "x" })).GetProperty("code").GetString());
        Assert.Equal("invalid_argument", (await Error(client, "set_device_control_mode", new { deviceId = _site.DeviceA, mode = "Local", reason = "" })).GetProperty("code").GetString());

        var reconnect = await Call(client, "reconnect_device", new { deviceId = _site.DeviceA });
        Assert.True(reconnect.GetProperty("success").GetBoolean(), reconnect.ToString());
        Assert.Contains(_site.DeviceA, _site.Diagnostics.Reconnects);
        Assert.Equal("unsupported", (await Error(client, "reconnect_device", new { deviceId = _site.GatewayDevice })).GetProperty("code").GetString());
        var missing = await Error(client, "reconnect_device", new { deviceId = _site.NoSnapshotDevice });
        Assert.Equal("data_unavailable", missing.GetProperty("code").GetString());
        Assert.True(missing.GetProperty("retryable").GetBoolean());
        Assert.Equal("not_found", (await Error(client, "reconnect_device", new { deviceId = 999999 })).GetProperty("code").GetString());
        await using var audit = await Db.CreateDbContextAsync();
        Assert.True(await audit.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "Device" && x.TargetId == _site.DeviceA.ToString() && x.Action.StartsWith("请求采集重连") && x.UserName == "mcp:lan"));
    }
}
