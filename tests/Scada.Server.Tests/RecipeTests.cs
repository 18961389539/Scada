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

// 配方管理：模板/配方/版本、比较、上载、经控制闭环下发（整体预检查、整批复核、回读、部分失败明确报告）、权限、审计、工程包与回滚、重启中断。
public sealed class RecipeTests : IClassFixture<ControlTests.ControlSite>
{
    private readonly ControlTests.ControlSite _site;
    public RecipeTests(ControlTests.ControlSite site) => _site = site;
    private ControlTests.FakeCollector Collector => _site.Services.GetRequiredService<ControlTests.FakeCollector>();
    private void Set(int tagId, double value) => _site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(tagId, value, TagQuality.Good, ScadaTime.NowMs()));
    private static string U() => Guid.NewGuid().ToString("N")[..5];
    private IDbContextFactory<ConfigDbContext> Db => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted ? default : await r.Content.ReadFromJsonAsync<JsonElement>();
    }
    private async Task<int> TagAsync(HttpClient admin, string name) =>
        (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, name + U())))).GetProperty("id").GetInt32();

    private async Task<(int Template, int Recipe, int[] Tags, string Name)> SetupAsync(HttpClient admin, bool dual = false, double[]? values = null)
    {
        var tags = new[] { await TagAsync(admin, "温度设定"), await TagAsync(admin, "压力设定"), await TagAsync(admin, "时间设定") };
        foreach (var t in tags) Set(t, 1);
        var name = "配方模板" + U();
        var template = await Ok(await admin.PostAsJsonAsync("/api/recipes/templates", new
        {
            name, areaId = _site.AreaA, dualApproval = dual,
            parameters = new object[]
            {
                new { key = "temp", name = "温度", tagId = tags[0], unit = "℃", min = 0, max = 200 },
                new { key = "press", name = "压力", tagId = tags[1], unit = "kPa", min = 0, max = 100 },
                new { key = "time", name = "时间", tagId = tags[2], unit = "s", min = 0, max = 1000 }
            }
        }));
        var tid = template.GetProperty("id").GetInt32();
        var v = values ?? [120, 50, 300];
        var recipe = await Ok(await admin.PostAsJsonAsync("/api/recipes", new
        {
            templateId = tid, name = "产品A", values = new[] { new { key = "temp", value = v[0] }, new { key = "press", value = v[1] }, new { key = "time", value = v[2] } }, note = "初版"
        }));
        return (tid, recipe.GetProperty("recipe").GetProperty("id").GetInt32(), tags, name);
    }

    private async Task<JsonElement> WaitDownloadAsync(HttpClient c, int id)
    {
        await _site.Services.GetRequiredService<RecipeService>().WaitAsync(id);
        return await Ok(await c.GetAsync($"/api/recipes/downloads/{id}"));
    }

    [Fact]
    public async Task TemplatesRecipesVersionsCompareAndUploadAreValidatedAndAudited()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA"); var cfg = await _site.ClientAsync("cfgA");
        var tag = await TagAsync(admin, "校验");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/recipes/templates", new { name = "坏" + U(), areaId = _site.AreaA, parameters = new[] { new { key = "1bad", name = "x", tagId = tag } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/recipes/templates", new { name = "坏" + U(), areaId = _site.AreaA, parameters = new[] { new { key = "a", name = "x", tagId = tag, min = 5, max = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/recipes/templates", new { name = "坏" + U(), areaId = _site.AreaA, parameters = new[] { new { key = "a", name = "x", tagId = _site.TagB + 99999 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/recipes/templates", new { name = "无权" + U(), areaId = _site.AreaA, parameters = new[] { new { key = "a", name = "x", tagId = tag } } })).StatusCode);
        // 配置员不能把 B 区测点放进模板
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/recipes/templates", new { name = "越区" + U(), areaId = _site.AreaA, parameters = new[] { new { key = "a", name = "x", tagId = _site.TagB } } })).StatusCode);

        var (tid, rid, tags, _) = await SetupAsync(admin);
        // 越限、缺参数、多余参数都拒绝
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/recipes/{rid}", new { templateId = tid, name = "产品A", version = 1, values = new[] { new { key = "temp", value = 999 }, new { key = "press", value = 1 }, new { key = "time", value = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/recipes/{rid}", new { templateId = tid, name = "产品A", version = 1, values = new[] { new { key = "temp", value = 1 } } })).StatusCode);
        var v2 = await Ok(await cfg.PutAsJsonAsync($"/api/recipes/{rid}", new { templateId = tid, name = "产品A", version = 1, note = "升温", values = new[] { new { key = "temp", value = 130 }, new { key = "press", value = 50 }, new { key = "time", value = 300 } } }));
        Assert.Equal(2, v2.GetProperty("recipe").GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await cfg.PutAsJsonAsync($"/api/recipes/{rid}", new { templateId = tid, name = "产品A", version = 1, values = new[] { new { key = "temp", value = 1 }, new { key = "press", value = 1 }, new { key = "time", value = 1 } } })).StatusCode);
        var versions = await Ok(await op.GetAsync($"/api/recipes/{rid}/versions"));
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal(120, versions[1].GetProperty("values")[0].GetProperty("value").GetDouble()); // 旧版本不可变

        var cmp = await Ok(await op.GetAsync($"/api/recipes/compare?left={rid}&leftVersion=1&right={rid}&rightVersion=2"));
        Assert.Equal(1, cmp.GetProperty("differences").GetInt32());
        Set(tags[0], 130); Set(tags[1], 50); Set(tags[2], 300);
        var live = await Ok(await op.GetAsync($"/api/recipes/compare?left={rid}&right=live"));
        Assert.Equal(0, live.GetProperty("differences").GetInt32());

        // 上载：操作员无配置权限；某个参数无有效值时拒绝；成功生成新配方
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/recipes/upload", new { templateId = tid, name = "现场" })).StatusCode);
        Set(tags[0], 130); Set(tags[1], 50);
        _site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(tags[2], 0, TagQuality.Bad, ScadaTime.NowMs()));
        var bad = await cfg.PostAsJsonAsync("/api/recipes/upload", new { templateId = tid, name = "现场" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); Assert.Contains("时间", await bad.Content.ReadAsStringAsync());
        // 配置重载可能清空实时缓存（全量并行运行时偶发），上载前重新写入全部现场值。
        Set(tags[0], 130); Set(tags[1], 50); Set(tags[2], 2000); // 超出模板上限：如实上载并提示
        var up = await Ok(await cfg.PostAsJsonAsync("/api/recipes/upload", new { templateId = tid, name = "现场" + U() }));
        Assert.Equal("upload", up.GetProperty("recipe").GetProperty("source").GetString());
        Assert.NotEmpty(up.GetProperty("warnings").EnumerateArray());
        Set(tags[0], 130); Set(tags[1], 50); Set(tags[2], 2000);
        var upAgain = await Ok(await cfg.PostAsJsonAsync("/api/recipes/upload", new { templateId = tid, recipeId = rid }));
        Assert.Equal(3, upAgain.GetProperty("recipe").GetProperty("version").GetInt32());

        await using var db = await Db.CreateDbContextAsync();
        var log = await db.OperationLogs.AsNoTracking().Where(x => x.TargetType == "Recipe" && x.TargetId == rid.ToString() && x.Action.Contains("v2")).SingleAsync();
        Assert.Contains("130", log.Details);
    }

    [Fact]
    public async Task DownloadPrechecksAllParametersWritesThroughControlPipelineAndVerifiesReadback()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA"); var cfg = await _site.ClientAsync("cfgA");
        var (_, rid, tags, _) = await SetupAsync(admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync($"/api/recipes/{rid}/download", new { version = 0, confirmed = true })).StatusCode); // 配置员无控制权
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/recipes/{rid}/download", new { version = 0, confirmed = false })).StatusCode);

        // 第二个参数挂“禁止操作”牌：整体预检查不通过，一个参数都不写。
        var lockout = await Ok(await admin.PostAsJsonAsync("/api/control/lockouts", new { tagId = tags[1], kind = "NoOperate", reason = "检修" }));
        var check = await Ok(await op.PostAsJsonAsync($"/api/recipes/{rid}/check", new { version = 0 }));
        Assert.False(check.GetProperty("ok").GetBoolean());
        var rejected = await Ok(await op.PostAsJsonAsync($"/api/recipes/{rid}/download", new { version = 0, confirmed = true }));
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        Assert.Contains("未下发任何参数", rejected.GetProperty("message").GetString());
        Assert.All(tags, t => Assert.Equal(0, Collector.Count(t)));
        await Ok(await admin.PostAsJsonAsync($"/api/control/lockouts/{lockout.GetProperty("id").GetInt32()}/remove", new { reason = "完成" }));

        // 控制策略限幅比配方范围更严：同样在预检查拦下
        await Ok(await admin.PutAsJsonAsync($"/api/control/points/{tags[0]}", new { requireConfirm = true, dualApproval = false, approvalTimeoutSec = 300, limitMin = 0, limitMax = 100, feedbackTimeoutMs = 10000 }));
        Assert.Equal("Rejected", (await Ok(await op.PostAsJsonAsync($"/api/recipes/{rid}/download", new { version = 0, confirmed = true }))).GetProperty("status").GetString());
        await Ok(await admin.DeleteAsync($"/api/control/points/{tags[0]}"));
        foreach (var t in tags) Set(t, 1);

        var started = await Ok(await op.PostAsJsonAsync($"/api/recipes/{rid}/download", new { version = 0, confirmed = true }));
        Assert.NotEqual("Pending", started.GetProperty("status").GetString());
        var done = await WaitDownloadAsync(op, started.GetProperty("id").GetInt32());
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.Equal(3, done.GetProperty("written").GetInt32());
        Assert.All(done.GetProperty("results").EnumerateArray(), r => { Assert.Equal("done", r.GetProperty("status").GetString()); Assert.Equal(r.GetProperty("target").GetDouble(), r.GetProperty("readBack").GetDouble()); });
        Assert.True(done.GetProperty("results")[0].GetProperty("before").ValueKind == JsonValueKind.Number, done.GetRawText());
        Assert.Equal(1, done.GetProperty("results")[0].GetProperty("before").GetDouble());
        await using var db = await Db.CreateDbContextAsync();
        // 每个参数都走控制闭环并留下控制写入审计（来源为配方）
        var writes = await db.OperationLogs.AsNoTracking().Where(x => x.TagId == tags[0] && x.Action.StartsWith("控制写入")).ToListAsync();
        Assert.Contains(writes, x => x.Details!.Contains("Recipe") && x.Details.Contains("产品A"));
        Assert.True(await db.OperationLogs.AnyAsync(x => x.TargetType == "RecipeDownload" && x.Action.StartsWith("配方下发结束") && x.Result.StartsWith("完成")));
    }

    [Fact]
    public async Task PartialFailureStopsAndIsReportedClearly()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var (_, rid, tags, _) = await SetupAsync(admin);
        lock (Collector.Writes) Collector.Fail.Add(tags[1]);
        try
        {
            var started = await Ok(await op.PostAsJsonAsync($"/api/recipes/{rid}/download", new { version = 0, confirmed = true }));
            var d = await WaitDownloadAsync(op, started.GetProperty("id").GetInt32());
            Assert.Equal("PartiallyApplied", d.GetProperty("status").GetString());
            var results = d.GetProperty("results");
            Assert.Equal("done", results[0].GetProperty("status").GetString());
            Assert.Equal("failed", results[1].GetProperty("status").GetString());
            Assert.Equal("skipped", results[2].GetProperty("status").GetString());
            Assert.Equal(0, Collector.Count(tags[2])); // 失败后不再继续写
            var message = d.GetProperty("message").GetString()!;
            Assert.Contains("部分下发", message); Assert.Contains("已写入 1/3", message); Assert.Contains("温度（原值", message);
        }
        finally { lock (Collector.Writes) Collector.Fail.Remove(tags[1]); }
    }

    [Fact]
    public async Task BatchDualApprovalRequiresAnotherOperator()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA"); var other = await _site.ClientAsync("watchA");
        var (_, rid, tags, _) = await SetupAsync(admin, dual: true);
        var pending = await Ok(await op.PostAsJsonAsync($"/api/recipes/{rid}/download", new { version = 0, confirmed = true }));
        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        Assert.All(tags, t => Assert.Equal(0, Collector.Count(t)));
        var approval = pending.GetProperty("approvalId").GetString();
        var list = await Ok(await other.GetAsync("/api/control/requests?status=Pending"));
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("request").GetProperty("id").GetString() == approval && x.GetProperty("recipe").GetProperty("recipeName").GetString() == "产品A");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/control/requests/{approval}/approve", new { comment = "自己批" })).StatusCode);
        await Ok(await other.PostAsJsonAsync($"/api/control/requests/{approval}/approve", new { comment = "核对无误" }));
        var d = await WaitDownloadAsync(op, pending.GetProperty("id").GetInt32());
        Assert.Equal("Completed", d.GetProperty("status").GetString());
        Assert.Equal("watchA", d.GetProperty("approvedBy").GetString());

        // 驳回：不写任何参数
        var again = await Ok(await op.PostAsJsonAsync($"/api/recipes/{rid}/download", new { version = 0, confirmed = true }));
        var before = Collector.Count(tags[0]);
        await Ok(await other.PostAsJsonAsync($"/api/control/requests/{again.GetProperty("approvalId").GetString()}/reject", new { comment = "不对" }));
        var rejected = await Ok(await op.GetAsync($"/api/recipes/downloads/{again.GetProperty("id").GetInt32()}"));
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        Assert.Equal(before, Collector.Count(tags[0]));
    }

    [Fact]
    public async Task RecipesAreInProjectPackageAndRollBack()
    {
        var admin = await _site.ClientAsync("admin");
        var (tid, rid, _, name) = await SetupAsync(admin);
        var package = JsonNode.Parse(await (await admin.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();
        var node = package["sections"]!["recipeTemplates"]!.AsArray().Single(x => x!["key"]!.GetValue<string>() == name)!["data"]!;
        Assert.DoesNotContain("tagId", node.ToJsonString());
        Assert.Contains("tagRef", node["parameters"]![0]!.ToJsonString());
        Assert.Equal(120, node["recipes"]![0]!["values"]![0]!["value"]!.GetValue<double>());

        var v1 = (await Ok(await admin.PostAsJsonAsync("/api/project/versions", new { note = "配方基线" + U() }))).GetProperty("id").GetInt32();
        await Ok(await admin.PutAsJsonAsync($"/api/recipes/{rid}", new { templateId = tid, name = "产品A", version = 1, values = new[] { new { key = "temp", value = 150 }, new { key = "press", value = 50 }, new { key = "time", value = 300 } } }));
        await Ok(await admin.PostAsJsonAsync("/api/recipes", new { templateId = tid, name = "产品B", values = new[] { new { key = "temp", value = 1 }, new { key = "press", value = 1 }, new { key = "time", value = 1 } } }));
        var diff = await Ok(await admin.GetAsync($"/api/project/diff?from={v1}&to=current"));
        Assert.Contains("recipeTemplates", diff.GetRawText());
        var impact = await Ok(await admin.PostAsync($"/api/project/versions/{v1}/rollback/preview", null));
        await Ok(await admin.PostAsJsonAsync($"/api/project/versions/{v1}/rollback", new { baseSha = impact.GetProperty("baseSha").GetString() }));
        await using var db = await Db.CreateDbContextAsync();
        var recipe = await db.Recipes.AsNoTracking().SingleAsync(x => x.Id == rid);
        Assert.Equal(3, recipe.CurrentVersion); // 回滚追加新版本，历史保留
        var current = await db.RecipeVersions.AsNoTracking().SingleAsync(x => x.RecipeId == rid && x.Version == 3);
        Assert.Contains("120", current.ValuesJson); Assert.Equal("project", current.Source);
        Assert.False(await db.Recipes.AnyAsync(x => x.TemplateId == tid && x.Name == "产品B"));
    }

    [Fact]
    public async Task RunningDownloadIsMarkedInterruptedAfterRestart()
    {
        int stale;
        await using (var db = await Db.CreateDbContextAsync())
        {
            var orphan = new RecipeDownload
            {
                RecipeId = 0, RecipeName = "崩溃配方", Version = 1, AreaId = _site.AreaA, Status = RecipeDownloadStatus.Running, RequestedBy = "admin", CreatedTs = ScadaTime.NowMs(), Written = 1,
                ResultsJson = JsonSerializer.Serialize(new[] { new RecipeParameterResult { Key = "a", Name = "甲", Status = "done", Target = 1 }, new RecipeParameterResult { Key = "b", Name = "乙", Target = 2 } }, RecipeService.Json)
            };
            db.RecipeDownloads.Add(orphan); await db.SaveChangesAsync(); stale = orphan.Id;
        }
        await _site.RestartAsync();
        RecipeDownload? d = null;
        for (var i = 0; i < 50; i++)
        {
            await using var after = await Db.CreateDbContextAsync();
            d = await after.RecipeDownloads.AsNoTracking().SingleAsync(x => x.Id == stale);
            if (d.Status != RecipeDownloadStatus.Running) break;
            await Task.Delay(100);
        }
        Assert.Equal(RecipeDownloadStatus.PartiallyApplied, d!.Status);
        Assert.Contains("中断", d.Message); Assert.Contains("不会自动续发", d.Message);
        Assert.Contains("\"skipped\"", d.ResultsJson);
    }
}
