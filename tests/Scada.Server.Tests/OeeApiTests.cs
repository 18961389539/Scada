using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Data;

namespace Scada.Server.Tests;

// OEE 接口：区域授权、产线配置、停机/产量修正、分析 KPI、导出、报表 OeeKpi 列。
public sealed class OeeApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public OeeApiTests(AuthorizationTests.Site site) => _site = site;
    private IDbContextFactory<ConfigDbContext> Factory() => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private const long M = 60_000, H = 3_600_000;
    private static async Task<JsonElement> Json(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync()); return await r.Content.ReadFromJsonAsync<JsonElement>(); }

    private async Task<(int LineA, int LineB, int ReasonId, long T0)> SeedAsync(int year)
    {
        var admin = await _site.ClientAsync("admin");
        var product = await Json(await admin.PostAsJsonAsync("/api/oee/products", new { name = $"P-{year}-{Guid.NewGuid():N}"[..20], idealCycleSec = 60.0, ratedUnitsPerHour = (double?)null }));
        var productId = product.GetProperty("id").GetInt32();
        var lineA = await Json(await admin.PostAsJsonAsync("/api/oee/lines", new
        {
            name = $"LA-{year}-{Guid.NewGuid():N}"[..20], areaId = _site.AreaA, idealCycleSec = 60.0, productId, enabled = true,
            stateMapJson = "{\"1\":\"Running\",\"0\":\"Stopped\",\"2\":\"Fault\"}"
        }));
        var lineB = await Json(await admin.PostAsJsonAsync("/api/oee/lines", new
        {
            name = $"LB-{year}-{Guid.NewGuid():N}"[..20], areaId = _site.AreaB, idealCycleSec = 60.0, enabled = true
        }));
        var reason = await Json(await admin.PostAsJsonAsync("/api/oee/reasons", new { code = $"R{year}{Guid.NewGuid():N}"[..12], name = "故障", category = "Availability", isPlanned = false, enabled = true }));
        var shift = await Json(await admin.PostAsJsonAsync("/api/oee/shifts", new { name = $"S{year}", areaId = _site.AreaA, startMinute = 0, endMinute = 1439, days = 127, enabled = true }));
        _ = shift;
        var t0 = new DateTimeOffset(year, 3, 1, 0, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        var lineAId = lineA.GetProperty("id").GetInt32();
        var lineBId = lineB.GetProperty("id").GetInt32();
        // 手工停机 + 产量修正
        await Json(await admin.PostAsJsonAsync("/api/oee/downtimes", new { lineId = lineAId, startTs = t0 + 45 * M, endTs = t0 + 55 * M, reasonId = reason.GetProperty("id").GetInt32(), state = "Fault", confirmed = true }));
        await Json(await admin.PostAsJsonAsync("/api/oee/corrections", new { lineId = lineAId, productId, periodFrom = t0, periodTo = t0 + H, goodDelta = 50.0, rejectDelta = 5.0, scrapDelta = 0.0, reason = "测试产量" }));
        await Json(await admin.PostAsJsonAsync("/api/oee/corrections", new { lineId = lineBId, periodFrom = t0, periodTo = t0 + H, goodDelta = 99.0, rejectDelta = 0.0, scrapDelta = 0.0, reason = "B区产量" }));
        return (lineAId, lineBId, reason.GetProperty("id").GetInt32(), t0);
    }

    [Fact]
    public async Task AnalyticsAreAreaScoped()
    {
        var (lineA, lineB, _, t0) = await SeedAsync(2031);
        var q = $"from={t0}&to={t0 + H}";
        var admin = await _site.ClientAsync("admin");
        var op = await _site.ClientAsync("opA");
        var all = (await Json(await admin.GetAsync($"/api/oee/analytics?{q}"))).GetProperty("kpi");
        Assert.True(all.GetProperty("good").GetDouble() >= 149); // A 50 + B 99
        var mine = (await Json(await op.GetAsync($"/api/oee/analytics?{q}"))).GetProperty("kpi");
        Assert.Equal(50, mine.GetProperty("good").GetDouble());
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/oee/analytics?{q}&areaId={_site.AreaB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/oee/analytics?{q}&lineId={lineB}")).StatusCode);
        var onlyA = (await Json(await admin.GetAsync($"/api/oee/analytics?{q}&lineId={lineA}"))).GetProperty("kpi");
        Assert.Equal(50, onlyA.GetProperty("good").GetDouble());
        Assert.True(onlyA.GetProperty("availability").GetDouble() > 0.7);
        Assert.True(onlyA.GetProperty("oee").GetDouble() > 0);
        Assert.True(onlyA.GetProperty("faultCount").GetInt32() >= 1);
    }

    [Fact]
    public async Task ConfigRequiresAreaConfigureAndViewerCanList()
    {
        var op = await _site.ClientAsync("opA");
        var cfg = await _site.ClientAsync("cfgA");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/oee/lines", new { name = "X", areaId = _site.AreaA, idealCycleSec = 60.0, enabled = true })).StatusCode);
        var line = await Json(await cfg.PostAsJsonAsync("/api/oee/lines", new { name = "CfgLine" + Guid.NewGuid().ToString("N")[..6], areaId = _site.AreaA, idealCycleSec = 30.0, enabled = true }));
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/oee/lines", new { name = "Other", areaId = _site.AreaB, idealCycleSec = 30.0, enabled = true })).StatusCode);
        var list = await Json(await op.GetAsync("/api/oee/lines"));
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("id").GetInt32() == line.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ExportAndOeeKpiReportColumn()
    {
        var (_, _, _, t0) = await SeedAsync(2032);
        var admin = await _site.ClientAsync("admin");
        var xlsx = await admin.GetAsync($"/api/oee/export?format=xlsx&from={t0}&to={t0 + H}");
        Assert.True(xlsx.IsSuccessStatusCode, await xlsx.Content.ReadAsStringAsync());
        Assert.True((await xlsx.Content.ReadAsByteArrayAsync()).Length > 100);
        var pdf = await admin.GetAsync($"/api/oee/export?format=pdf&from={t0}&to={t0 + H}");
        Assert.True(pdf.IsSuccessStatusCode);

        var cfg = await _site.ClientAsync("cfgA");
        Assert.Equal(HttpStatusCode.BadRequest, (await cfg.PostAsJsonAsync("/api/report-templates", new
        {
            name = "OEE坏" + Guid.NewGuid().ToString("N")[..4], period = "Day", areaId = _site.AreaA,
            columns = new object[] { new { key = "x", title = "X", kind = "OeeKpi", output = "nope", decimals = 2 } }
        })).StatusCode);
        var template = await Json(await cfg.PostAsJsonAsync("/api/report-templates", new
        {
            name = "OEE报" + Guid.NewGuid().ToString("N")[..4], period = "Day", areaId = _site.AreaA,
            columns = new object[]
            {
                new { key = "oee", title = "OEE", kind = "OeeKpi", output = "oee", decimals = 3 },
                new { key = "a", title = "可用率", kind = "OeeKpi", output = "availability", decimals = 3 },
                new { key = "good", title = "好品", kind = "OeeKpi", output = "good", decimals = 0 }
            }
        }));
        var run = await Json(await cfg.PostAsJsonAsync($"/api/report-templates/{template.GetProperty("id").GetInt32()}/generate", new { at = t0 + 30 * M }));
        await using var db = await Factory().CreateDbContextAsync();
        var stored = await db.ReportRuns.AsNoTracking().SingleAsync(x => x.Id == run.GetProperty("id").GetInt64());
        var data = JsonDocument.Parse(stored.DataJson).RootElement;
        var summary = data.GetProperty("summary").GetProperty("values");
        Assert.Equal(JsonValueKind.Number, summary[2].ValueKind); // good
        Assert.True(summary[2].GetDouble() >= 50);
    }

    [Fact]
    public async Task ThresholdsNeedGlobalConfigure()
    {
        var cfg = await _site.ClientAsync("cfgA");
        var admin = await _site.ClientAsync("admin");
        var cur = await Json(await admin.GetAsync("/api/oee/thresholds"));
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PutAsJsonAsync("/api/oee/thresholds", new { thresholds = cur.GetProperty("thresholds"), revision = cur.GetProperty("revision").GetInt64() })).StatusCode);
        var th = cur.GetProperty("thresholds");
        var body = new Dictionary<string, object?>();
        foreach (var p in th.EnumerateObject()) body[p.Name] = p.Value.ValueKind switch
        {
            JsonValueKind.Number => p.Value.TryGetInt64(out var l) && p.Value.GetDouble() == l ? l : p.Value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => p.Value.ToString()
        };
        body["targetOee"] = 0.8;
        var saved = await Json(await admin.PutAsJsonAsync("/api/oee/thresholds", new { thresholds = body, revision = cur.GetProperty("revision").GetInt64() }));
        Assert.Equal(0.8, saved.GetProperty("thresholds").GetProperty("targetOee").GetDouble());
    }
}
