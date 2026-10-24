using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;

namespace Scada.Server.Tests;

// 现场控制闭环：确认、限幅/变化率、许可互锁、就地/远程、挂牌、双人复核、独立反馈超时、顺控、画面控制与自动化写入。
public sealed class ControlTests : IClassFixture<ControlTests.ControlSite>
{
    public sealed class FakeCollector(IRealtimeCache cache) : ICollectorControl
    {
        public readonly List<(int TagId, double Value)> Writes = [];
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => [];
        public void RequestReload() { }
        // 测试用：写入这些测点时模拟设备拒绝（未写入）。
        public readonly HashSet<int> Fail = [];
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default)
        {
            lock (Writes) { if (Fail.Contains(tag.Id)) return Task.FromResult(new CommandExecutionResult(false, "模拟设备拒绝写入", null)); }
            lock (Writes) Writes.Add((tag.Id, value));
            cache.Set(new TagValue(tag.Id, value, TagQuality.Good, ScadaTime.NowMs()));
            return Task.FromResult(new CommandExecutionResult(true, "写入成功", value));
        }
        public int Count(int tagId) { lock (Writes) return Writes.Count(x => x.TagId == tagId); }
    }

    public class ControlSite : AuthorizationTests.Site
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            services.RemoveAll<ICollectorControl>();
            services.AddSingleton<FakeCollector>(); services.AddSingleton<ICollectorControl>(sp => sp.GetRequiredService<FakeCollector>());
        }
        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            var admin = await ClientAsync("admin");
            var device = await admin.GetFromJsonAsync<JsonElement>($"/api/devices/{DeviceA}");
            var body = JsonSerializer.SerializeToNode(DeviceBody(device, AreaA))!.AsObject(); body["enabled"] = true;
            var r = await admin.PutAsJsonAsync($"/api/devices/{DeviceA}", body); r.EnsureSuccessStatusCode();
            ControlCommandService.FeedbackPollMs = 20;
        }
    }

    private readonly ControlSite _site;
    public ControlTests(ControlSite site) => _site = site;
    private FakeCollector Collector => _site.Services.GetRequiredService<FakeCollector>();
    private void Set(int tagId, double value) => _site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(tagId, value, TagQuality.Good, ScadaTime.NowMs()));
    private static string U() => Guid.NewGuid().ToString("N")[..5];

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode == HttpStatusCode.NoContent || r.StatusCode == HttpStatusCode.Accepted ? default : await r.Content.ReadFromJsonAsync<JsonElement>();
    }
    private async Task<int> TagAsync(HttpClient admin, string name, int? device = null) =>
        (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(device ?? _site.DeviceA, name + U())))).GetProperty("id").GetInt32();
    private static object Point(bool confirm = false, bool dual = false, string? permit = null, string? permitMessage = null, double? min = null, double? max = null, double? step = null,
        string? feedback = null, int feedbackMs = 10000) =>
        new { requireConfirm = confirm, dualApproval = dual, approvalTimeoutSec = 300, permitExpression = permit, permitMessage, limitMin = min, limitMax = max, maxStep = step, feedbackExpression = feedback, feedbackTimeoutMs = feedbackMs };
    private static async Task<JsonElement> WriteAsync(HttpClient c, int tag, double value, bool confirmed = true) =>
        await Ok(await c.PostAsJsonAsync("/api/commands", new { tagId = tag, value, requestId = Guid.NewGuid(), confirmed }));

    [Fact]
    public async Task ConfirmLimitsRateAndPermitAreEnforcedAndAudited()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var tag = await TagAsync(admin, "阀位"); var permit = await TagAsync(admin, "许可");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsJsonAsync($"/api/control/points/{tag}", Point())).StatusCode); // 操作员无配置权限
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/control/points/{tag}", Point(permit: "#999999 > 0"))).StatusCode);
        await Ok(await admin.PutAsJsonAsync($"/api/control/points/{tag}", Point(confirm: true, permit: $"#{permit} > 0", permitMessage: "泵未就绪", min: 0, max: 100, step: 20)));
        Set(tag, 10); Set(permit, 0);

        var check = await Ok(await op.PostAsJsonAsync("/api/control/check", new { tagId = tag, value = 50 }));
        var reasons = check.GetProperty("refusals").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(reasons, x => x.Contains("单次变化 40 超过限制 20")); Assert.Contains(reasons, x => x.Contains("泵未就绪"));
        Assert.True(check.GetProperty("requireConfirm").GetBoolean());

        Set(permit, 1);
        var unconfirmed = await WriteAsync(op, tag, 15, confirmed: false);
        Assert.False(unconfirmed.GetProperty("success").GetBoolean()); Assert.Contains("确认", unconfirmed.GetProperty("message").GetString());
        Assert.Contains("超出操作限幅", (await WriteAsync(op, tag, 120)).GetProperty("message").GetString());
        Assert.Equal(0, Collector.Count(tag));
        var ok = await WriteAsync(op, tag, 25);
        Assert.True(ok.GetProperty("success").GetBoolean(), ok.ToString()); Assert.Equal(25, ok.GetProperty("readBack").GetDouble()); Assert.Equal(1, Collector.Count(tag));
        Assert.NotNull(await _site.LastLogAsync(x => x.TagId == tag && x.Action.StartsWith("控制被拒绝") && x.Result!.Contains("超出操作限幅")));
        Assert.NotNull(await _site.LastLogAsync(x => x.TagId == tag && x.Action.StartsWith("控制写入") && x.Result!.StartsWith("成功")));
        // 许可条件引用的测点无有效值视为不满足
        _site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(permit, 1, TagQuality.Bad, ScadaTime.NowMs()));
        Assert.Contains("无有效值", (await WriteAsync(op, tag, 30)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task LocalModeAndLockoutsBlockCommandsWithAreaPermissions()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA"); var cfg = await _site.ClientAsync("cfgA");
        var tag = await TagAsync(admin, "开关"); Set(tag, 0);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsJsonAsync($"/api/control/modes/{_site.DeviceB}", new { mode = "Local", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync($"/api/control/modes/{_site.DeviceA}", new { mode = "Local", reason = "" })).StatusCode);
        await Ok(await op.PutAsJsonAsync($"/api/control/modes/{_site.DeviceA}", new { mode = "Local", reason = "现场调试" }));
        Assert.Contains("就地控制", (await WriteAsync(op, tag, 1)).GetProperty("message").GetString());
        await Ok(await op.PutAsJsonAsync($"/api/control/modes/{_site.DeviceA}", new { mode = "Remote", reason = "调试结束" }));
        Assert.NotNull(await _site.LastLogAsync(x => x.Action.Contains("切换控制权") && x.Action.Contains("就地")));

        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/control/lockouts", new { tagId = _site.TagB, kind = "NoOperate", reason = "x" })).StatusCode);
        var no = await Ok(await op.PostAsJsonAsync("/api/control/lockouts", new { tagId = tag, kind = "NoOperate", reason = "管线泄漏" }));
        Assert.Contains("禁止操作", (await WriteAsync(op, tag, 1)).GetProperty("message").GetString());
        Assert.Contains("禁止操作", (await WriteAsync(admin, tag, 1)).GetProperty("message").GetString()); // 管理员同样被拒
        await Ok(await op.PostAsJsonAsync($"/api/control/lockouts/{no.GetProperty("id").GetInt32()}/remove", new { reason = "已修复" }));

        var maint = await Ok(await admin.PostAsJsonAsync("/api/control/lockouts", new { deviceId = _site.DeviceA, kind = "Maintenance", reason = "检修 A 区" }));
        Assert.Contains("仅挂牌人可操作", (await WriteAsync(op, tag, 1)).GetProperty("message").GetString());
        Assert.True((await WriteAsync(admin, tag, 1)).GetProperty("success").GetBoolean()); // 挂牌人本人可检修调试
        var id = maint.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/control/lockouts/{id}/remove", new { reason = "x" })).StatusCode); // 非挂牌人
        await Ok(await cfg.PostAsJsonAsync($"/api/control/lockouts/{id}/remove", new { reason = "检修完成，代为摘牌" })); // 区域配置管理员
        Assert.True((await WriteAsync(op, tag, 0)).GetProperty("success").GetBoolean());
        Assert.NotNull(await _site.LastLogAsync(x => x.Action.StartsWith("摘牌") && x.UserName == "cfgA"));
    }

    [Fact]
    public async Task DualApprovalNeedsAnotherAuthorizedUserAndRechecksOnApproval()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA"); var second = await _site.ClientAsync("watchA");
        var tag = await TagAsync(admin, "主阀"); Set(tag, 0);
        await Ok(await admin.PutAsJsonAsync($"/api/control/points/{tag}", Point(dual: true, max: 100)));
        var pending = await WriteAsync(op, tag, 40);
        Assert.True(pending.GetProperty("pending").GetBoolean()); Assert.Equal(0, Collector.Count(tag));
        var id = pending.GetProperty("approvalId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/control/requests/{id}/approve", new { comment = "" })).StatusCode); // 不能自批
        var list = await Ok(await second.GetAsync("/api/control/requests?status=Pending"));
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("request").GetProperty("id").GetGuid() == id);
        var approved = await Ok(await second.PostAsJsonAsync($"/api/control/requests/{id}/approve", new { comment = "已核对" }));
        Assert.True(approved.GetProperty("result").GetProperty("success").GetBoolean()); Assert.Equal(1, Collector.Count(tag));
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync($"/api/control/requests/{id}/approve", new { comment = "" })).StatusCode); // 不会重复执行
        var log = await _site.LastLogAsync(x => x.TagId == tag && x.Action.StartsWith("控制写入"));
        Assert.Contains("watchA", log!.Details); Assert.Equal("opA", log.UserName);

        // 批准时重新检查：申请后挂了禁止操作牌，批准也不执行。
        var again = (await WriteAsync(op, tag, 50)).GetProperty("approvalId").GetGuid();
        var lockout = await Ok(await admin.PostAsJsonAsync("/api/control/lockouts", new { tagId = tag, kind = "NoOperate", reason = "临时禁止" }));
        var refused = await Ok(await second.PostAsJsonAsync($"/api/control/requests/{again}/approve", new { comment = "" }));
        Assert.False(refused.GetProperty("result").GetProperty("success").GetBoolean()); Assert.Equal("Failed", refused.GetProperty("request").GetProperty("status").GetString());
        await Ok(await admin.PostAsJsonAsync($"/api/control/lockouts/{lockout.GetProperty("id").GetInt32()}/remove", new { reason = "解除" }));
        // 驳回与超时
        var rejected = (await WriteAsync(op, tag, 60)).GetProperty("approvalId").GetGuid();
        Assert.Equal("Rejected", (await Ok(await second.PostAsJsonAsync($"/api/control/requests/{rejected}/reject", new { comment = "不需要" }))).GetProperty("request").GetProperty("status").GetString());
        var expired = (await WriteAsync(op, tag, 60)).GetProperty("approvalId").GetGuid();
        await using (var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync())
            await db.ControlRequests.Where(x => x.Id == expired).ExecuteUpdateAsync(x => x.SetProperty(r => r.ExpiresTs, 1));
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync($"/api/control/requests/{expired}/approve", new { comment = "" })).StatusCode);
        Assert.Equal(1, Collector.Count(tag));
        // 自动化（计算脚本）不能写需要复核的测点
        var script = await _site.Services.GetRequiredService<ControlCommandService>().ExecuteAsync(tag, 10, null, "脚本");
        Assert.False(script.Success); Assert.Contains("双人复核", script.Message);
    }

    [Fact]
    public async Task FeedbackTimeoutIsFailureAndSequencesStopAtFirstFailedStep()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var valve = await TagAsync(admin, "阀门"); var position = await TagAsync(admin, "阀位反馈"); var pump = await TagAsync(admin, "泵"); var last = await TagAsync(admin, "末步");
        Set(valve, 0); Set(position, 0); Set(pump, 0); Set(last, 0);
        await Ok(await admin.PutAsJsonAsync($"/api/control/points/{valve}", Point(feedback: $"#{position} >= 90", feedbackMs: 300)));
        var timedOut = await WriteAsync(op, valve, 1);
        Assert.False(timedOut.GetProperty("success").GetBoolean()); Assert.Contains("反馈超时", timedOut.GetProperty("message").GetString());
        Assert.Equal("Unknown", timedOut.GetProperty("outcome").GetString());
        Set(position, 95);
        Assert.True((await WriteAsync(op, valve, 1)).GetProperty("success").GetBoolean());

        Set(position, 0);
        object Step(string name, int tag, double value, string? confirm = null, int timeout = 300) => new { name, tagId = tag, value, confirmExpression = confirm, timeoutMs = timeout, delayMs = 0 };
        var bad = await op.PostAsJsonAsync("/api/control/sequences", new { name = "x", areaId = _site.AreaA, steps = new[] { Step("a", valve, 1) } });
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
        var name = "启泵" + U();
        var sequence = await Ok(await admin.PostAsJsonAsync("/api/control/sequences", new
        {
            name, areaId = _site.AreaA, description = "开阀→等反馈→启泵→末步", dualApproval = false,
            steps = new[] { Step("开泵前确认", pump, 0, $"#{pump} == 0"), Step("启泵", pump, 1, $"#{position} >= 90"), Step("末步", last, 1) }
        }));
        var id = sequence.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/control/sequences/{id}/start", new { confirmed = false })).StatusCode);
        var started = await Ok(await op.PostAsJsonAsync($"/api/control/sequences/{id}/start", new { confirmed = true }));
        var runId = started.GetProperty("runId").GetInt32();
        await _site.Services.GetRequiredService<ControlWorkflowService>().WaitAsync(runId);
        var run = await Ok(await op.GetAsync($"/api/control/runs/{runId}"));
        Assert.Equal("Failed", run.GetProperty("status").GetString()); Assert.Contains("第 2 步", run.GetProperty("message").GetString());
        var steps = run.GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("status").GetString()!).ToArray();
        Assert.Equal(["done", "failed", "skipped"], steps);
        Assert.Equal(0, Collector.Count(last));
        Assert.NotNull(await _site.LastLogAsync(x => x.Action == $"顺控结束 {name}" && x.Result!.StartsWith("失败")));

        // 需复核的顺控：另一人批准后执行，且完成条件满足时全部完成。
        Set(position, 95);
        var current = await Ok(await admin.GetAsync("/api/control/sequences"));
        var revision = current.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == id).GetProperty("revision").GetInt64();
        await Ok(await admin.PutAsJsonAsync($"/api/control/sequences/{id}", new { name, areaId = _site.AreaA, dualApproval = true, revision,
            steps = new[] { Step("启泵", pump, 1, $"#{position} >= 90"), Step("末步", last, 1) } }));
        await Ok(await admin.PutAsJsonAsync($"/api/control/points/{last}", Point(dual: true))); // 步骤测点需复核：只有已复核的顺控可写
        var pending = await Ok(await op.PostAsJsonAsync($"/api/control/sequences/{id}/start", new { confirmed = true }));
        Assert.True(pending.GetProperty("pending").GetBoolean());
        var approval = await Ok(await (await _site.ClientAsync("watchA")).PostAsJsonAsync($"/api/control/requests/{pending.GetProperty("approvalId").GetGuid()}/approve", new { comment = "同意" }));
        var second = approval.GetProperty("runId").GetInt32();
        await _site.Services.GetRequiredService<ControlWorkflowService>().WaitAsync(second);
        var done = await Ok(await op.GetAsync($"/api/control/runs/{second}"));
        Assert.Equal("Completed", done.GetProperty("status").GetString()); Assert.Equal("watchA", done.GetProperty("approvedBy").GetString());
        Assert.Equal(1, Collector.Count(last));
    }

    [Fact]
    public async Task ScreenRuntimeCommandsGoThroughTheSameControlChecks()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var tag = await TagAsync(admin, "画面阀"); Set(tag, 0);
        await Ok(await admin.PutAsJsonAsync($"/api/control/points/{tag}", Point(confirm: true, max: 50)));
        var u = U();
        var screen = (await Ok(await admin.PostAsJsonAsync("/api/screens", new { name = "控制画面" + u }))).GetProperty("id").GetInt32();
        var document = new { schemaVersion = 1, width = 800, height = 600, background = "#101b2d", nodes = new object[] {
            new { id = "open", type = "button", x = 10, y = 10, width = 100, height = 40, text = "开", action = new { type = "command", tagId = tag, value = 30 } },
            new { id = "over", type = "button", x = 10, y = 60, width = 100, height = 40, text = "超限", action = new { type = "command", tagId = tag, value = 80 } } } };
        await Ok(await admin.PutAsJsonAsync($"/api/screens/{screen}", new { name = "控制画面" + u, document, revision = 1 }));
        var published = await Ok(await admin.PostAsJsonAsync($"/api/screens/{screen}/publish", new { revision = 2 }));
        var rev = published.GetProperty("publishedRevision").GetInt32();
        async Task<JsonElement> Act(string node, bool confirmed) => await Ok(await op.PostAsJsonAsync($"/api/screens/{screen}/actions/{node}", new { publishedRevision = rev, requestId = Guid.NewGuid(), confirmed }));
        Assert.Contains("确认", (await Act("open", false)).GetProperty("message").GetString());
        Assert.Contains("超出操作限幅", (await Act("over", true)).GetProperty("message").GetString());
        Assert.True((await Act("open", true)).GetProperty("success").GetBoolean());
        var log = await _site.LastLogAsync(x => x.TagId == tag && x.Action.StartsWith("控制写入"));
        Assert.Contains("Screen", log!.Details); Assert.Contains("控制画面", log.Details);
    }
}
