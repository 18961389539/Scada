using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

// 控制策略/顺控进工程包与快照回滚；服务重启后运行中的顺控标记为中断。
public sealed class ControlOpsTests : IClassFixture<ControlTests.ControlSite>
{
    private readonly ControlTests.ControlSite _site;
    public ControlOpsTests(ControlTests.ControlSite site) => _site = site;
    private static string U() => Guid.NewGuid().ToString("N")[..5];
    private void Set(int tagId, double value) => _site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(tagId, value, TagQuality.Good, ScadaTime.NowMs()));

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted ? default : await r.Content.ReadFromJsonAsync<JsonElement>();
    }
    private async Task<int> TagAsync(HttpClient admin, string name) =>
        (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, name + U())))).GetProperty("id").GetInt32();
    private static object Point(string? permit, double? max) => new { requireConfirm = true, dualApproval = false, approvalTimeoutSec = 300, permitExpression = permit, limitMin = 0, limitMax = max, feedbackTimeoutMs = 10000 };

    [Fact]
    public async Task ControlPointsAndSequencesAreInProjectPackageAndRollBack()
    {
        var admin = await _site.ClientAsync("admin");
        var valve = await TagAsync(admin, "包阀"); var permit = await TagAsync(admin, "包许可");
        await Ok(await admin.PutAsJsonAsync($"/api/control/points/{valve}", Point($"#{permit} > 0", 80)));
        var name = "包顺控" + U();
        var seq = await Ok(await admin.PostAsJsonAsync("/api/control/sequences", new
        {
            name, areaId = _site.AreaA, dualApproval = false,
            steps = new[] { new { name = "开阀", tagId = valve, value = 1.0, confirmExpression = $"#{permit} == 1", timeoutMs = 500, delayMs = 0 } }
        }));
        var seqId = seq.GetProperty("id").GetInt32();
        var lockout = await Ok(await admin.PostAsJsonAsync("/api/control/lockouts", new { tagId = valve, reason = "检修挂牌", kind = "Maintenance" }));

        var package = JsonNode.Parse(await (await admin.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();
        var text = package.ToJsonString();
        Assert.DoesNotContain("检修挂牌", text); // 挂牌是运行状态，不进包
        var point = package["sections"]!["controlPoints"]!.AsArray().Single(x => x!["key"]!.GetValue<string>().Contains("包阀"))!["data"];
        Assert.Contains("#{", point!["permitExpression"]!.GetValue<string>());
        Assert.DoesNotContain("tagId", point.ToJsonString());
        var seqNode = package["sections"]!["controlSequences"]!.AsArray().Single(x => x!["key"]!.GetValue<string>() == name)!["data"];
        Assert.Contains("#{", seqNode!["steps"]![0]!["confirmExpression"]!.GetValue<string>());

        var v1 = (await Ok(await admin.PostAsJsonAsync("/api/project/versions", new { note = "控制基线" + U() }))).GetProperty("id").GetInt32();
        await Ok(await admin.PutAsJsonAsync($"/api/control/points/{valve}", Point(null, 50)));
        await Ok(await admin.DeleteAsync($"/api/control/sequences/{seqId}"));
        var diff = await Ok(await admin.GetAsync($"/api/project/diff?from={v1}&to=current"));
        Assert.Contains("controlSequences", diff.GetRawText()); Assert.Contains("controlPoints", diff.GetRawText());

        var impact = await Ok(await admin.PostAsync($"/api/project/versions/{v1}/rollback/preview", null));
        await Ok(await admin.PostAsJsonAsync($"/api/project/versions/{v1}/rollback", new { baseSha = impact.GetProperty("baseSha").GetString() }));
        await using var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        var restored = await db.ControlPoints.AsNoTracking().SingleAsync(x => x.TagId == valve);
        Assert.Equal(80, restored.LimitMax); Assert.Equal($"#{permit} > 0", restored.PermitExpression);
        var back = await db.ControlSequences.AsNoTracking().SingleAsync(x => x.Name == name);
        Assert.Contains($"#{permit} == 1", back.StepsJson);
        Assert.True(await db.ControlLockouts.AnyAsync(x => x.TagId == valve && x.RemovedTs == null)); // 回滚不动挂牌
    }

    [Fact]
    public async Task RunningSequenceIsMarkedInterruptedAfterRestart()
    {
        var admin = await _site.ClientAsync("admin");
        var tag = await TagAsync(admin, "重启泵"); var never = await TagAsync(admin, "永不");
        Set(tag, 0); Set(never, 0);
        var name = "重启顺控" + U();
        var seq = await Ok(await admin.PostAsJsonAsync("/api/control/sequences", new
        {
            name, areaId = _site.AreaA, dualApproval = false,
            steps = new[] { new { name = "启泵并长等", tagId = tag, value = 1.0, confirmExpression = $"#{never} == 1", timeoutMs = 600000, delayMs = 0 } }
        }));
        var runId = (await Ok(await admin.PostAsJsonAsync($"/api/control/sequences/{seq.GetProperty("id").GetInt32()}/start", new { confirmed = true }))).GetProperty("runId").GetInt32();
        await Task.Delay(300);

        // 另插入一条“进程崩溃遗留”的 Running 记录（无对应执行任务）。
        int stale;
        await using (var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync())
        {
            var orphan = new ControlSequenceRun { SequenceId = seq.GetProperty("id").GetInt32(), SequenceName = name, AreaId = _site.AreaA, Status = ControlRunStatus.Running, StartedTs = ScadaTime.NowMs(), StartedBy = "admin", StepsJson = "[]" };
            db.ControlSequenceRuns.Add(orphan); await db.SaveChangesAsync(); stale = orphan.Id;
        }

        await _site.RestartAsync();
        await using var after = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        ControlSequenceRun? s = null;
        for (var i = 0; i < 50; i++)
        {
            s = await after.ControlSequenceRuns.AsNoTracking().SingleAsync(x => x.Id == stale);
            if (s.Status != ControlRunStatus.Running) break;
            await Task.Delay(100);
        }
        var r = await after.ControlSequenceRuns.AsNoTracking().SingleAsync(x => x.Id == runId);
        Assert.Equal(ControlRunStatus.Failed, r.Status); Assert.Contains("中断", r.Message);
        Assert.Contains("\"failed\"", r.StepsJson);
        Assert.Equal(ControlRunStatus.Failed, s!.Status); Assert.Contains("中断", s.Message);
        Assert.True(await after.OperationLogs.AnyAsync(x => x.Action == $"顺控中断 {name}"));
        var api = await Ok(await (await _site.ClientAsync("admin")).GetAsync($"/api/control/runs/{runId}"));
        Assert.Equal("Failed", api.GetProperty("status").GetString());
    }
}

public sealed class ControlConfirmDefaultTests : IClassFixture<ControlConfirmDefaultTests.ConfirmSite>
{
    public sealed class ConfirmSite : ControlTests.ControlSite
    {
        protected override void ConfigureSettings(IWebHostBuilder b) => b.UseSetting("Scada:ControlRequireConfirmByDefault", "true");
    }
    private readonly ConfirmSite _site;
    public ControlConfirmDefaultTests(ConfirmSite site) => _site = site;

    [Fact]
    public async Task UnconfiguredTagsRequireConfirmWhenSettingIsOn()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var tag = (await (await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "默认确认" + Guid.NewGuid().ToString("N")[..5]))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var check = await (await op.PostAsJsonAsync("/api/control/check", new { tagId = tag, value = 1 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(check.GetProperty("requireConfirm").GetBoolean());
        var refused = await (await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(refused.GetProperty("success").GetBoolean()); Assert.Contains("确认", refused.GetProperty("message").GetString());
        var ok = await (await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(ok.GetProperty("success").GetBoolean(), ok.GetRawText());
    }
}
