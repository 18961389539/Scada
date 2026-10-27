using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Calculations;

namespace Scada.Server.Tests;

// 计算任务接口：区域权限、脚本写测点需 Control、差分审计、派生测点自动维护。
public sealed class CalcJobApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public CalcJobApiTests(AuthorizationTests.Site site) => _site = site;

    private static string Name(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public async Task CSharpIsDisabledByDefaultAndChecksPermissionsBeforeCompilation()
    {
        var admin = await _site.ClientAsync("admin"); var cfg = await _site.ClientAsync("cfgA"); var op = await _site.ClientAsync("opA");
        var capabilities = await admin.GetFromJsonAsync<JsonElement>("/api/calc-jobs/csharp/capabilities");
        Assert.False(capabilities.GetProperty("enabled").GetBoolean());
        var request = new { name = Name("CSharp"), kind = "CSharp", areaId = _site.AreaA, script = "context.Output(\"answer\", 42);" };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PostAsJsonAsync("/api/calc-jobs/csharp/compile", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/calc-jobs/csharp/preview", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/calc-jobs/csharp/compile", new { request.name, request.kind, request.areaId, request.script, readTags = _site.TagB.ToString() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/calc-jobs/csharp/preview", new { request.name, request.kind, request.areaId, request.script, writeTags = _site.TagA.ToString() })).StatusCode);
    }

    [Fact]
    public async Task JobsAreAreaScopedAuditedAndMaintainDerivedTags()
    {
        var cfg = await _site.ClientAsync("cfgA"); var op = await _site.ClientAsync("opA"); var admin = await _site.ClientAsync("admin");
        var name = Name("泵");
        var created = await cfg.PostAsJsonAsync("/api/calc-jobs", new { name, kind = "RunHours", areaId = _site.AreaA, inputTagId = _site.TagA, maintenanceHours = 500, resetPeriod = "Shift", shiftStarts = "08:00,20:00" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var job = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = job.GetProperty("id").GetInt32();
        var audit = await _site.LastLogAsync(x => x.UserName == "cfgA" && x.TargetType == "CalcJob" && x.TargetId == id.ToString());
        Assert.Contains("maintenanceHours", audit!.Details);

        await using (var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync())
        {
            var derived = await db.Tags.Where(x => x.JobId == id).ToListAsync();
            Assert.Equal(6, derived.Count);
            Assert.All(derived, x => { Assert.Equal(TagKind.Derived, x.Kind); Assert.Equal(_site.AreaA, x.AreaId); Assert.False(x.Writable); });
            // 派生测点不能当普通测点改或删。
            var tagId = derived[0].Id;
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/tags/{tagId}")).StatusCode);
        }

        // 越区：B 区或引用 B 区测点都被拒并审计。
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/calc-jobs", new { name = Name("x"), kind = "RunHours", areaId = _site.AreaB, inputTagId = _site.TagA })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/calc-jobs", new { name = Name("x"), kind = "Energy", areaId = _site.AreaA, inputTagId = _site.TagB })).StatusCode);
        Assert.StartsWith("拒绝", (await _site.LastLogAsync(x => x.UserName == "cfgA" && x.Action.StartsWith("创建计算任务")))!.Result);
        // 配置员没有 Control，不能让脚本写现场测点；管理员可以。
        var script = new { name = Name("脚本"), kind = "Script", areaId = _site.AreaA, intervalMs = 1000, script = $"#{_site.TagA} = 1", readTags = "", writeTags = _site.TagA.ToString() };
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/calc-jobs", script)).StatusCode);
        var scripted = await admin.PostAsJsonAsync("/api/calc-jobs", script);
        Assert.Equal(HttpStatusCode.Created, scripted.StatusCode);
        Assert.Equal("admin", (await scripted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runAsUserName").GetString());
        // 脚本语法/白名单错误返回 400。
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/calc-jobs", new { name = Name("坏"), kind = "Script", areaId = _site.AreaA, intervalMs = 1000, script = $"@x = #{_site.TagB}", readTags = "" })).StatusCode);

        // 列表按区域过滤；操作员可复位/确认维护（Control），配置员不行。
        var list = await op.GetFromJsonAsync<JsonElement[]>("/api/calc-jobs");
        Assert.Contains(list!, x => x.GetProperty("job").GetProperty("id").GetInt32() == id);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsync($"/api/calc-jobs/{id}/maintenance-done", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/calc-jobs/{id}/maintenance-done", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/calc-jobs/{id}/reset", null)).StatusCode);
        Assert.NotNull(await _site.LastLogAsync(x => x.UserName == "opA" && x.Action == $"确认设备维护：{name}"));
        var snapshots = await op.GetFromJsonAsync<JsonElement[]>($"/api/calc-jobs/{id}/snapshots");
        Assert.Equal(2, snapshots!.Length);

        // 修改：版本冲突 409；改名后派生测点随之改名并记差分。
        Assert.Equal(HttpStatusCode.Conflict, (await cfg.PutAsJsonAsync($"/api/calc-jobs/{id}", new { name, kind = "RunHours", areaId = _site.AreaA, inputTagId = _site.TagA, revision = 99 })).StatusCode);
        var renamed = name + "改";
        Assert.Equal(HttpStatusCode.OK, (await cfg.PutAsJsonAsync($"/api/calc-jobs/{id}", new { name = renamed, kind = "RunHours", areaId = _site.AreaA, inputTagId = _site.TagA, revision = 1 })).StatusCode);
        var diff = await _site.LastLogAsync(x => x.UserName == "cfgA" && x.Action == $"修改计算任务：{renamed}");
        Assert.Contains(renamed, diff!.Details);
        Assert.Equal(HttpStatusCode.NoContent, (await cfg.DeleteAsync($"/api/calc-jobs/{id}?revision=2")).StatusCode);
        await using var check = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        Assert.False(await check.Tags.AnyAsync(x => x.JobId == id));
    }
}

public sealed class CSharpCalcJobApiTests(CSharpCalcJobApiTests.Site site) : IClassFixture<CSharpCalcJobApiTests.Site>
{
    public sealed class Site : AuthorizationTests.Site
    {
        protected override void ConfigureServices(IServiceCollection services) => services.PostConfigure<CSharpScriptOptions>(options => options.Enabled = true);
    }

    [Fact]
    public async Task CompileAndPreviewReturnDiagnosticsAndIntentionsWithoutSavingOrSubmittingCommands()
    {
        var admin = await site.ClientAsync("admin");
        await using var db = await site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        var jobsBefore = await db.CalcJobs.CountAsync(); var tagsBefore = await db.Tags.CountAsync();
        site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(site.TagA, 21, TagQuality.Good, ScadaTime.NowMs()));
        var request = new { name = "CSharp preview", kind = "CSharp", areaId = site.AreaA,
            script = $"context.Output(\"answer\", context.Read({site.TagA}) * 2); context.Write({site.TagA}, 1); context.Log(\"preview log\");",
            readTags = site.TagA.ToString(), writeTags = site.TagA.ToString() };
        var compile = await admin.PostAsJsonAsync("/api/calc-jobs/csharp/compile", request);
        Assert.Equal(HttpStatusCode.OK, compile.StatusCode);
        Assert.True((await compile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean());
        var preview = await admin.PostAsJsonAsync("/api/calc-jobs/csharp/preview", request);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var result = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean()); Assert.Equal(42, result.GetProperty("outputs").GetProperty("answer").GetDouble());
        Assert.Equal(1, result.GetProperty("writes").GetProperty(site.TagA.ToString()).GetDouble());
        Assert.Equal("preview log", result.GetProperty("logs")[0].GetString());
        Assert.Equal(jobsBefore, await db.CalcJobs.CountAsync()); Assert.Equal(tagsBefore, await db.Tags.CountAsync());
        Assert.False(await db.OperationLogs.AnyAsync(log => log.Action.StartsWith("计算任务写测点")));
        var invalid = await admin.PostAsJsonAsync("/api/calc-jobs/csharp/compile", new { request.name, request.kind, request.areaId, script = "context.Output(\"broken\", );" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Contains("script.cs", await invalid.Content.ReadAsStringAsync());
        site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(site.TagA, 21, TagQuality.Bad, ScadaTime.NowMs()));
        var badInput = await admin.PostAsJsonAsync("/api/calc-jobs/csharp/preview", request);
        var rejected = await badInput.Content.ReadFromJsonAsync<JsonElement>(); Assert.False(rejected.GetProperty("success").GetBoolean());
        Assert.Empty(rejected.GetProperty("writes").EnumerateObject());
    }
}
