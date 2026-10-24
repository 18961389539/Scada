using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Reports;

namespace Scada.Server.Tests;

// 报表中心：模板按区域授权并差分审计；归档只追加、按版本保留；下载校验 SHA256；定时生成只做一次。
public sealed class ReportApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public ReportApiTests(AuthorizationTests.Site site) => _site = site;
    private IDbContextFactory<ConfigDbContext> Factory() => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();

    private object Body(string name, int area, int tag, long revision = 0, bool schedule = false) => new
    {
        name, period = "Day", areaId = area, shiftStarts = "08:00,20:00", scheduleEnabled = schedule, scheduleDelayMin = 10, revision,
        columns = new object[]
        {
            new { key = "avg", title = "平均温度", kind = "TagAvg", tagId = tag, decimals = 2, unit = "℃" },
            new { key = "max", title = "最高温度", kind = "TagMax", tagId = tag, decimals = 2 },
            new { key = "alarms", title = "报警次数", kind = "AlarmCount", decimals = 0 },
            new { key = "span", title = "温差", kind = "Formula", formula = "$max - $avg", decimals = 2 }
        }
    };

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task TemplatesAreAreaScopedValidatedAndDiffAudited()
    {
        var cfg = await _site.ClientAsync("cfgA"); var op = await _site.ClientAsync("opA");
        var name = "日报" + Guid.NewGuid().ToString("N")[..4];
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/report-templates", Body(name, _site.AreaA, _site.TagA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/report-templates", Body(name, _site.AreaB, _site.TagB))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/report-templates", Body(name, _site.AreaA, _site.TagB))).StatusCode);
        var bad = new { name, period = "Day", areaId = _site.AreaA, columns = new object[] { new { key = "f", title = "F", kind = "Formula", formula = "$later + 1" }, new { key = "later", title = "L", kind = "TagAvg", tagId = _site.TagA } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await cfg.PostAsJsonAsync("/api/report-templates", bad)).StatusCode);

        var created = await Json(await cfg.PostAsJsonAsync("/api/report-templates", Body(name, _site.AreaA, _site.TagA)));
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal(4, created.GetProperty("columns").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await cfg.PutAsJsonAsync($"/api/report-templates/{id}", Body(name + "x", _site.AreaA, _site.TagA, revision: 99))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cfg.PutAsJsonAsync($"/api/report-templates/{id}", Body(name + "x", _site.AreaA, _site.TagA, revision: 1))).StatusCode);
        var log = await _site.LastLogAsync(x => x.UserName == "cfgA" && x.Action == $"修改报表模板：{name}x");
        using var diff = JsonDocument.Parse(log!.Details!);
        Assert.Equal(name + "x", diff.RootElement.GetProperty("changes").GetProperty("name").GetProperty("new").GetString());

        Assert.Contains(await op.GetFromJsonAsync<JsonElement[]>("/api/report-templates") ?? [], x => x.GetProperty("id").GetInt32() == id);
        var admin = await _site.ClientAsync("admin");
        var other = await Json(await admin.PostAsJsonAsync("/api/report-templates", Body("B日报" + Guid.NewGuid().ToString("N")[..4], _site.AreaB, _site.TagB)));
        Assert.DoesNotContain(await op.GetFromJsonAsync<JsonElement[]>("/api/report-templates") ?? [], x => x.GetProperty("id").GetInt32() == other.GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync($"/api/report-templates/{other.GetProperty("id").GetInt32()}/generate", new { })).StatusCode);
    }

    [Fact]
    public async Task GeneratedRunsAreVersionedImmutableAndTamperChecked()
    {
        var cfg = await _site.ClientAsync("cfgA"); var op = await _site.ClientAsync("opA");
        var template = await Json(await cfg.PostAsJsonAsync("/api/report-templates", Body("版本" + Guid.NewGuid().ToString("N")[..4], _site.AreaA, _site.TagA)));
        var id = template.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/report-templates/{id}/generate", new { })).StatusCode);
        var v1 = await Json(await cfg.PostAsJsonAsync($"/api/report-templates/{id}/generate", new { }));
        var v2 = await Json(await cfg.PostAsJsonAsync($"/api/report-templates/{id}/generate", new { }));
        Assert.Equal(v1.GetProperty("periodKey").GetString(), v2.GetProperty("periodKey").GetString());
        Assert.Equal(1, v1.GetProperty("version").GetInt32()); Assert.Equal(2, v2.GetProperty("version").GetInt32());
        var runId = v1.GetProperty("id").GetInt64();

        var detail = await op.GetFromJsonAsync<JsonElement>($"/api/report-runs/{runId}");
        Assert.Equal(4, detail.GetProperty("table").GetProperty("columns").GetArrayLength());
        var pdf = await op.GetAsync($"/api/report-runs/{runId}/download?format=pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
        var xlsx = await op.GetAsync($"/api/report-runs/{runId}/download?format=xlsx");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        // 无修改/删除接口。
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await cfg.DeleteAsync($"/api/report-runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await cfg.PutAsJsonAsync($"/api/report-runs/{runId}", new { })).StatusCode);

        // 篡改归档文件后下载被拒绝。
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var run = await db.ReportRuns.AsNoTracking().SingleAsync(x => x.Id == runId);
            File.SetAttributes(run.XlsxPath, FileAttributes.Normal);
            await File.AppendAllTextAsync(run.XlsxPath, "x");
        }
        Assert.Equal(HttpStatusCode.Conflict, (await op.GetAsync($"/api/report-runs/{runId}/download?format=xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/report-runs/{v2.GetProperty("id").GetInt64()}/download?format=xlsx")).StatusCode);

        // 删除模板后历史报表保留。
        var latest = await cfg.GetFromJsonAsync<JsonElement[]>("/api/report-templates");
        var revision = latest!.Single(x => x.GetProperty("id").GetInt32() == id).GetProperty("revision").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await cfg.DeleteAsync($"/api/report-templates/{id}?revision={revision}")).StatusCode);
        Assert.Equal(2, (await op.GetFromJsonAsync<JsonElement[]>($"/api/report-runs?templateId={id}"))!.Length);
        var admin = await _site.ClientAsync("admin");
        var b = await Json(await admin.PostAsJsonAsync("/api/report-templates", Body("B版本" + Guid.NewGuid().ToString("N")[..4], _site.AreaB, _site.TagB)));
        var bRun = await Json(await admin.PostAsJsonAsync($"/api/report-templates/{b.GetProperty("id").GetInt32()}/generate", new { }));
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/report-runs/{bRun.GetProperty("id").GetInt64()}/download?format=pdf")).StatusCode);
        Assert.DoesNotContain(await op.GetFromJsonAsync<JsonElement[]>("/api/report-runs") ?? [], x => x.GetProperty("id").GetInt64() == bRun.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task SchedulerGeneratesEachCompletedPeriodOnce()
    {
        var cfg = await _site.ClientAsync("cfgA");
        var template = await Json(await cfg.PostAsJsonAsync("/api/report-templates", Body("定时" + Guid.NewGuid().ToString("N")[..4], _site.AreaA, _site.TagA, schedule: true)));
        var id = template.GetProperty("id").GetInt32();
        var scheduler = _site.Services.GetRequiredService<ReportScheduler>();
        var now = ScadaTime.NowMs();
        await scheduler.RunOnceAsync(now, CancellationToken.None);
        await scheduler.RunOnceAsync(now + 1000, CancellationToken.None);
        await using var db = await Factory().CreateDbContextAsync();
        var runs = await db.ReportRuns.AsNoTracking().Where(x => x.TemplateId == id).ToListAsync();
        Assert.Single(runs);
        Assert.Equal("schedule", runs[0].Trigger);
        Assert.True(runs[0].ToTs <= now);
    }
}
