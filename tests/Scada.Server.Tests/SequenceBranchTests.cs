using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;

namespace Scada.Server.Tests;

// 带分支的顺控：条件分支、并行（all/any）汇合、跳转循环上限、失败处理（继续/转向）、静态校验、旧格式兼容与工程包。
public sealed class SequenceBranchTests : IClassFixture<ControlTests.ControlSite>
{
    private readonly ControlTests.ControlSite _site;
    public SequenceBranchTests(ControlTests.ControlSite site) => _site = site;
    private ControlTests.FakeCollector Collector => _site.Services.GetRequiredService<ControlTests.FakeCollector>();
    private void Set(int tagId, double value) => _site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(tagId, value, TagQuality.Good, ScadaTime.NowMs()));
    private static string U() => Guid.NewGuid().ToString("N")[..5];

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted ? default : await r.Content.ReadFromJsonAsync<JsonElement>();
    }
    private async Task<int> TagAsync(HttpClient admin, string name) =>
        (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, name + U())))).GetProperty("id").GetInt32();
    private Task<HttpResponseMessage> SaveAsync(HttpClient c, string name, object[] steps) =>
        c.PostAsJsonAsync("/api/control/sequences", new { name, areaId = _site.AreaA, dualApproval = false, steps });
    private async Task<JsonElement> RunAsync(HttpClient c, int sequenceId)
    {
        var runId = (await Ok(await c.PostAsJsonAsync($"/api/control/sequences/{sequenceId}/start", new { confirmed = true }))).GetProperty("runId").GetInt32();
        await _site.Services.GetRequiredService<ControlWorkflowService>().WaitAsync(runId);
        return await Ok(await c.GetAsync($"/api/control/runs/{runId}"));
    }
    private static JsonElement Step(JsonElement run, string id) => run.GetProperty("steps").EnumerateArray().Single(x => x.GetProperty("stepId").GetString() == id);
    private static object Write(string id, int tag, double value, string? next = null, string? onFailure = null, string? onFailureGoto = null, string? confirm = null, int timeoutMs = 2000) =>
        new { id, type = "write", name = id, tagId = tag, value, next, onFailure, onFailureGoto, confirmExpression = confirm, timeoutMs, delayMs = 0 };

    [Fact]
    public async Task ConditionalBranchWritesOnlyTheSelectedPath()
    {
        var admin = await _site.ClientAsync("admin");
        var sel = await TagAsync(admin, "选择"); var a = await TagAsync(admin, "甲"); var b = await TagAsync(admin, "乙");
        var seq = await Ok(await SaveAsync(admin, "条件" + U(), [
            new { id = "c", type = "condition", name = "选择", expression = $"#{sel} == 1", then = "wa", @else = "wb" },
            Write("wa", a, 5, next: "done"), Write("wb", b, 7, next: "done"), new { id = "done", type = "end", name = "结束" }]));
        Set(sel, 0);
        var run = await RunAsync(admin, seq.GetProperty("id").GetInt32());
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal(0, Collector.Count(a)); Assert.Equal(1, Collector.Count(b));
        Assert.Equal("skipped", Step(run, "wa").GetProperty("status").GetString());
        Assert.Contains("不成立", Step(run, "c").GetProperty("message").GetString());

        // 条件引用的测点无有效值：按失败处理，不猜测分支
        _site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(sel, 0, TagQuality.Bad, ScadaTime.NowMs()));
        var bad = await RunAsync(admin, seq.GetProperty("id").GetInt32());
        Assert.Equal("Failed", bad.GetProperty("status").GetString()); Assert.Contains("无法判定", bad.GetProperty("message").GetString());
        Assert.Equal(1, Collector.Count(b));
    }

    [Fact]
    public async Task ParallelAllAndAnyJoin()
    {
        var admin = await _site.ClientAsync("admin");
        var a = await TagAsync(admin, "并甲"); var b = await TagAsync(admin, "并乙"); var never = await TagAsync(admin, "永不"); var after = await TagAsync(admin, "汇后");
        Set(never, 0);
        var all = await Ok(await SaveAsync(admin, "并行全" + U(), [
            new { id = "p", type = "parallel", name = "同时开", branches = new[] { "ba", "bb" }, join = "j" },
            Write("ba", a, 1, next: "j"), Write("bb", b, 1, next: "j"),
            new { id = "j", type = "join", name = "汇合", joinMode = "all" }, Write("after", after, 1)]));
        var run = await RunAsync(admin, all.GetProperty("id").GetInt32());
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal(1, Collector.Count(a)); Assert.Equal(1, Collector.Count(b)); Assert.Equal(1, Collector.Count(after));
        Assert.Equal("done", Step(run, "j").GetProperty("status").GetString());

        // all：一个分支失败（完成条件永不成立）→ 整体失败，汇合后的步骤不执行
        var allFail = await Ok(await SaveAsync(admin, "并行全败" + U(), [
            new { id = "p", type = "parallel", name = "同时开", branches = new[] { "ba", "bb" }, join = "j" },
            Write("ba", a, 2, next: "j"), Write("bb", b, 2, next: "j", confirm: $"#{never} == 1", timeoutMs: 300),
            new { id = "j", type = "join", name = "汇合", joinMode = "all" }, Write("after", after, 2)]));
        var failed = await RunAsync(admin, allFail.GetProperty("id").GetInt32());
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal(1, Collector.Count(after));
        Assert.Equal("failed", Step(failed, "j").GetProperty("status").GetString());

        // any：一个分支完成即汇合，另一条在等待中的分支被停止，不再继续写
        var any = await Ok(await SaveAsync(admin, "并行任一" + U(), [
            new { id = "p", type = "parallel", name = "任一", branches = new[] { "ba", "w" }, join = "j" },
            Write("ba", a, 3, next: "j"),
            new { id = "w", type = "wait", name = "长等", expression = $"#{never} == 1", timeoutMs = 600000 }, Write("bw", b, 99, next: "j"),
            new { id = "j", type = "join", name = "汇合", joinMode = "any" }, Write("after", after, 3)]));
        var started = DateTime.UtcNow;
        var anyRun = await RunAsync(admin, any.GetProperty("id").GetInt32());
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        Assert.Equal("Completed", anyRun.GetProperty("status").GetString());
        Assert.Equal(2, Collector.Count(after));
        Assert.DoesNotContain(Collector.Writes.ToArray(), x => x.TagId == b && x.Value == 99);
    }

    [Fact]
    public async Task LoopGuardAndFailureHandling()
    {
        var admin = await _site.ClientAsync("admin");
        var a = await TagAsync(admin, "循环"); var safe = await TagAsync(admin, "安全"); var locked = await TagAsync(admin, "锁定");
        var loop = await Ok(await SaveAsync(admin, "循环" + U(), [Write("w", a, 1), new { id = "again", type = "jump", name = "再来", target = "w", maxIterations = 2 }]));
        var run = await RunAsync(admin, loop.GetProperty("id").GetInt32());
        Assert.Equal("Failed", run.GetProperty("status").GetString());
        Assert.Contains("循环超过上限 2 次", run.GetProperty("message").GetString());
        Assert.Equal(3, Step(run, "w").GetProperty("executions").GetInt32());
        Assert.Equal(3, Collector.Count(a));

        await Ok(await admin.PostAsJsonAsync("/api/control/lockouts", new { tagId = locked, kind = "NoOperate", reason = "试验" }));
        var divert = await Ok(await SaveAsync(admin, "失败转向" + U(), [
            Write("x", locked, 1, onFailure: "goto", onFailureGoto: "safe"), Write("normal", a, 5, next: "end"),
            Write("safe", safe, 0, next: "end"), new { id = "end", type = "end", name = "结束" }]));
        var d = await RunAsync(admin, divert.GetProperty("id").GetInt32());
        Assert.Equal("Failed", d.GetProperty("status").GetString());
        Assert.Contains("失败处理", d.GetProperty("message").GetString());
        Assert.Equal(1, Collector.Count(safe)); Assert.Equal("skipped", Step(d, "normal").GetProperty("status").GetString());

        var cont = await Ok(await SaveAsync(admin, "失败继续" + U(), [Write("x", locked, 1, onFailure: "continue"), Write("y", safe, 2)]));
        var c = await RunAsync(admin, cont.GetProperty("id").GetInt32());
        Assert.Equal("Completed", c.GetProperty("status").GetString());
        Assert.Contains("按配置继续", c.GetProperty("message").GetString());
        Assert.Equal(2, Collector.Count(safe));
    }

    [Fact]
    public async Task GraphValidationRejectsBadSequences()
    {
        var admin = await _site.ClientAsync("admin"); var a = await TagAsync(admin, "校验");
        async Task Bad(string expected, object[] steps)
        {
            var r = await SaveAsync(admin, "坏" + U(), steps);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); Assert.Contains(expected, await r.Content.ReadAsStringAsync());
        }
        await Bad("没有次数上限的循环", [Write("a", a, 1), Write("b", a, 2, next: "a")]);
        await Bad("不可达", [Write("a", a, 1, next: "c"), Write("b", a, 2), new { id = "c", type = "end", name = "c" }]);
        await Bad("缺少汇合", [new { id = "p", type = "parallel", name = "p", branches = new[] { "a", "b" } }, Write("a", a, 1), Write("b", a, 2)]);
        await Bad("循环上限", [Write("a", a, 1), new { id = "j", type = "jump", name = "j", target = "a" }]);
        await Bad("测点 #999999 不存在", [new { id = "c", type = "condition", name = "c", expression = "#999999 > 0", then = "a" }, Write("a", a, 1)]);
    }

    [Fact]
    public async Task LegacySequencesStillRunAndBranchingSequencesRoundTripThroughPackage()
    {
        var admin = await _site.ClientAsync("admin");
        var a = await TagAsync(admin, "旧甲"); var b = await TagAsync(admin, "旧乙"); var sel = await TagAsync(admin, "包选");
        var legacyName = "旧顺控" + U(); int legacyId;
        await using (var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync())
        {
            // 升级前保存的旧格式步骤（无 id/type）
            var legacy = new ControlSequence { Name = legacyName, AreaId = _site.AreaA, StepsJson = $"[{{\"name\":\"一\",\"tagId\":{a},\"value\":1,\"timeoutMs\":1000,\"delayMs\":0}},{{\"name\":\"二\",\"tagId\":{b},\"value\":2,\"timeoutMs\":1000,\"delayMs\":0}}]" };
            db.ControlSequences.Add(legacy); await db.SaveChangesAsync(); legacyId = legacy.Id;
        }
        var run = await RunAsync(admin, legacyId);
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal(["s1", "s2"], run.GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("stepId").GetString()));

        var name = "包分支" + U();
        await Ok(await SaveAsync(admin, name, [
            new { id = "c", type = "condition", name = "选择", expression = $"#{sel} == 1", then = "wa", @else = "wb" },
            Write("wa", a, 5, next: "done"), Write("wb", b, 7, next: "done"), new { id = "done", type = "end", name = "结束" }]));
        var package = JsonNode.Parse(await (await admin.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();
        var sequences = package["sections"]!["controlSequences"]!.AsArray();
        var legacyNode = sequences.Single(x => x!["key"]!.GetValue<string>() == legacyName)!["data"]!["steps"]![0]!.AsObject();
        Assert.False(legacyNode.ContainsKey("type")); Assert.False(legacyNode.ContainsKey("id")); // 旧格式导出不变
        var branchNode = sequences.Single(x => x!["key"]!.GetValue<string>() == name)!["data"]!["steps"]![0]!;
        Assert.Equal("c", branchNode["id"]!.GetValue<string>()); Assert.Contains("#{", branchNode["expression"]!.GetValue<string>());

        var v1 = (await Ok(await admin.PostAsJsonAsync("/api/project/versions", new { note = "分支基线" + U() }))).GetProperty("id").GetInt32();
        var seq = (await Ok(await admin.GetAsync("/api/control/sequences"))).EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        await Ok(await admin.DeleteAsync($"/api/control/sequences/{seq.GetProperty("id").GetInt32()}"));
        var impact = await Ok(await admin.PostAsync($"/api/project/versions/{v1}/rollback/preview", null));
        await Ok(await admin.PostAsJsonAsync($"/api/project/versions/{v1}/rollback", new { baseSha = impact.GetProperty("baseSha").GetString() }));
        var back = (await Ok(await admin.GetAsync("/api/control/sequences"))).EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var steps = back.GetProperty("steps");
        Assert.Equal("condition", steps[0].GetProperty("type").GetString()); Assert.Equal("wb", steps[0].GetProperty("else").GetString());
        Assert.Equal($"#{sel} == 1", steps[0].GetProperty("expression").GetString());
    }
}
