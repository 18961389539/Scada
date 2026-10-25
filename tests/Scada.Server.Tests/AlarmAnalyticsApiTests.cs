using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;

namespace Scada.Server.Tests;

// 报警统计分析接口：区域授权过滤、筛选、下钻、阈值配置（全局配置权限 + 差分审计 + 版本冲突）、导出、报表中心报警 KPI 列。
public sealed class AlarmAnalyticsApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public AlarmAnalyticsApiTests(AuthorizationTests.Site site) => _site = site;
    private IDbContextFactory<ConfigDbContext> Factory() => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private const long M = 60_000;

    // 每个测试用不同的历史时段，互不干扰。
    private async Task<long> SeedAsync(int year)
    {
        var t0 = new DateTimeOffset(year, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await using var db = await Factory().CreateDbContextAsync();
        for (var i = 0; i < 12; i++)
            db.AlarmEvents.Add(new AlarmEvent { TagId = _site.TagA, DeviceId = _site.DeviceA, Level = AlarmLevel.Warning, Message = "A高", StartTs = t0 + i * 10_000, EndTs = t0 + i * 10_000 + 3000 });
        for (var i = 0; i < 5; i++)
            db.AlarmEvents.Add(new AlarmEvent { TagId = _site.TagB, DeviceId = _site.DeviceB, Level = AlarmLevel.Critical, Message = "B高", StartTs = t0 + 30 * M + i * M, AckTs = t0 + 30 * M + i * M + 20_000, AckUser = "admin" });
        await db.SaveChangesAsync();
        return t0;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync()); return await r.Content.ReadFromJsonAsync<JsonElement>(); }

    [Fact]
    public async Task ResultsAreAreaScopedAndFilterable()
    {
        var t0 = await SeedAsync(2011);
        var q = $"from={t0}&to={t0 + 60 * M}";
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var all = (await Json(await admin.GetAsync($"/api/alarm-analytics?{q}"))).GetProperty("kpi");
        Assert.Equal(17, all.GetProperty("total").GetInt32());
        Assert.Single(all.GetProperty("floods").EnumerateArray()); // 12 条/10 分钟 > 10
        Assert.Equal("A高", all.GetProperty("floods")[0].GetProperty("firstOut").GetProperty("message").GetString());
        var mine = (await Json(await op.GetAsync($"/api/alarm-analytics?{q}"))).GetProperty("kpi");
        Assert.Equal(12, mine.GetProperty("total").GetInt32());
        Assert.DoesNotContain(mine.GetProperty("top").EnumerateArray(), x => x.GetProperty("message").GetString() == "B高");
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/alarm-analytics?{q}&areaId={_site.AreaB}")).StatusCode);
        Assert.Equal(5, (await Json(await admin.GetAsync($"/api/alarm-analytics?{q}&areaId={_site.AreaB}"))).GetProperty("kpi").GetProperty("total").GetInt32());
        Assert.Equal(5, (await Json(await admin.GetAsync($"/api/alarm-analytics?{q}&levels=2"))).GetProperty("kpi").GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/alarm-analytics?from={t0}&to={t0 - 1}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/alarm-analytics?from={t0}&to={t0 + 400 * 86_400_000L}")).StatusCode);

        // 下钻与趋势同样受区域约束。
        var events = await Json(await op.GetAsync($"/api/alarm-analytics/events?{q}&kind=fleeting"));
        Assert.Equal(12, events.GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await op.GetAsync($"/api/alarm-analytics/events?{q}&source=t{_site.TagB}"))).GetProperty("total").GetInt32());
        var trend = await Json(await op.GetAsync($"/api/alarm-analytics/trend?{q}"));
        Assert.Equal("hour", trend.GetProperty("granularity").GetString());
        Assert.Equal(12, trend.GetProperty("points").EnumerateArray().Sum(p => p.GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task ThresholdsNeedGlobalConfigureAreAuditedAndVersioned()
    {
        var admin = await _site.ClientAsync("admin"); var cfg = await _site.ClientAsync("cfgA");
        var current = await Json(await admin.GetAsync("/api/alarm-analytics/thresholds"));
        var revision = current.GetProperty("revision").GetInt64();
        var th = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.GetProperty("thresholds").GetRawText())!;
        var edited = th.ToDictionary(x => x.Key, x => (object)x.Value); edited["fleetingSec"] = 25;
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PutAsJsonAsync("/api/alarm-analytics/thresholds", new { thresholds = edited, revision })).StatusCode);
        var bad = new Dictionary<string, object>(edited) { ["floodEndPer10Min"] = 999 };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/alarm-analytics/thresholds", new { thresholds = bad, revision })).StatusCode);
        var saved = await Json(await admin.PutAsJsonAsync("/api/alarm-analytics/thresholds", new { thresholds = edited, revision }));
        Assert.Equal(revision + 1, saved.GetProperty("revision").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/alarm-analytics/thresholds", new { thresholds = edited, revision })).StatusCode);
        Assert.Equal(25, (await Json(await cfg.GetAsync("/api/alarm-analytics/thresholds"))).GetProperty("thresholds").GetProperty("fleetingSec").GetInt32());
        var log = await _site.LastLogAsync(x => x.Action == "修改报警分析阈值");
        Assert.Contains("fleetingSec", log!.Details);
        // 改瞬时秒数会安排预聚合重算。
        await using var db = await Factory().CreateDbContextAsync();
        Assert.NotNull((await db.AlarmRollupStates.SingleAsync()).RebuildFromTs);
        edited["fleetingSec"] = 10;
        await Json(await admin.PutAsJsonAsync("/api/alarm-analytics/thresholds", new { thresholds = edited, revision = revision + 1 }));
    }

    [Fact]
    public async Task ExportsExcelAndPdfAndRebuildIsAudited()
    {
        var t0 = await SeedAsync(2012);
        var op = await _site.ClientAsync("opA");
        var xlsx = await op.GetAsync($"/api/alarm-analytics/export?format=xlsx&from={t0}&to={t0 + 60 * M}");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString((await xlsx.Content.ReadAsByteArrayAsync())[..2]));
        var pdfRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/alarm-analytics/export?format=pdf&from={t0}&to={t0 + 60 * M}");
        pdfRequest.Headers.Add("X-Scada-Locale", "en-US");
        var pdf = await op.SendAsync(pdfRequest);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
        Assert.Equal(HttpStatusCode.BadRequest, (await op.GetAsync($"/api/alarm-analytics/export?format=doc&from={t0}&to={t0 + 60 * M}")).StatusCode);
        Assert.NotNull(await _site.LastLogAsync(x => x.UserName == "opA" && x.Action == "导出报警统计分析"));

        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/alarm-analytics/rollups/rebuild", new { days = 3 })).StatusCode);
        var admin = await _site.ClientAsync("admin");
        await Json(await admin.PostAsJsonAsync("/api/alarm-analytics/rollups/rebuild", new { days = 3 }));
        var status = await Json(await admin.GetAsync("/api/alarm-analytics/rollups"));
        Assert.True(status.GetProperty("watermarkTs").GetInt64() > 0);
        Assert.NotNull(await _site.LastLogAsync(x => x.Action == "重算报警预聚合：最近 3 天"));
    }

    [Fact]
    public async Task ReportTemplateWithAlarmKpiColumns()
    {
        var cfg = await _site.ClientAsync("cfgA");
        Assert.Equal(HttpStatusCode.BadRequest, (await cfg.PostAsJsonAsync("/api/report-templates", new
        {
            name = "KPI坏" + Guid.NewGuid().ToString("N")[..4], period = "Day", areaId = _site.AreaA,
            columns = new object[] { new { key = "x", title = "X", kind = "AlarmKpi", output = "nope", decimals = 0 } }
        })).StatusCode);
        var template = await Json(await cfg.PostAsJsonAsync("/api/report-templates", new
        {
            name = "报警KPI" + Guid.NewGuid().ToString("N")[..4], period = "Day", areaId = _site.AreaA,
            columns = new object[]
            {
                new { key = "ann", title = "需报警", kind = "AlarmKpi", output = "annunciated", decimals = 0 },
                new { key = "peak", title = "峰值", kind = "AlarmKpi", output = "peak10Min", decimals = 0 },
                new { key = "fleet", title = "瞬时", kind = "AlarmKpi", output = "fleeting", decimals = 0 }
            }
        }));
        var t0 = await SeedAsync(2013);
        var run = await Json(await cfg.PostAsJsonAsync($"/api/report-templates/{template.GetProperty("id").GetInt32()}/generate", new { at = t0 + 60 * M }));
        await using var db = await Factory().CreateDbContextAsync();
        var stored = await db.ReportRuns.AsNoTracking().SingleAsync(x => x.Id == run.GetProperty("id").GetInt64());
        var data = JsonDocument.Parse(stored.DataJson).RootElement;
        var summary = data.GetProperty("summary").GetProperty("values");
        Assert.Equal(12, summary[0].GetDouble()); // 只统计模板区域 A
        Assert.Equal(12, summary[1].GetDouble());
        Assert.Equal(12, summary[2].GetDouble());
        Assert.False(string.IsNullOrEmpty(stored.PdfSha256));
    }
}
