using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Spc;

namespace Scada.Server.Tests;

public sealed class SpcQualityApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public SpcQualityApiTests(AuthorizationTests.Site site) => _site = site;
    private IDbContextFactory<RuntimeDbContext> Runtime() => _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private static object ChartBody(int areaId, int tagId, string name) => new
    {
        name, enabled = true, areaId, tagId, chartType = "Individuals", subgroupSize = 1,
        samplingIntervalSec = 86400, baselineSubgroups = 5, autoLimits = false,
        centerLine = 10.0, upperControlLimit = 13.0, lowerControlLimit = 7.0,
        specUpperLimit = 15.0, specLowerLimit = 5.0,
        ruleBeyond3Sigma = true, ruleTwoOfThreeBeyond2Sigma = false, ruleFourOfFiveBeyond1Sigma = false,
        ruleEightOnOneSide = false, ruleRangeBeyondLimit = false, description = "", revision = 0
    };

    [Fact]
    public async Task SpcChartPermissionSamplingRulesAndAlarmLifecycle()
    {
        var opA = await _site.ClientAsync("opA");
        var cfgA = await _site.ClientAsync("cfgA");
        var admin = await _site.ClientAsync("admin");
        var name = Unique("SPC-");
        var body = ChartBody(_site.AreaA, _site.TagA, name);

        Assert.Equal(HttpStatusCode.Forbidden, (await opA.PostAsJsonAsync("/api/spc/charts", body)).StatusCode);
        var created = await Json(await cfgA.PostAsJsonAsync("/api/spc/charts", body));
        var chartId = created.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await cfgA.PostAsJsonAsync("/api/spc/charts", ChartBody(_site.AreaB, _site.TagB, name + "-B"))).StatusCode);

        var cache = _site.Services.GetRequiredService<IRealtimeCache>();
        var monitor = _site.Services.GetRequiredService<SpcMonitorService>();
        var now = ScadaTime.NowMs();
        cache.Set(new TagValue(_site.TagA, 20, TagQuality.Good, now));
        await monitor.SampleOnceAsync(now, default, forceReload: true);

        var sampleRows = await Json(await opA.GetAsync($"/api/spc/charts/{chartId}/samples?from={now - 1000}&to={now + 1000}"));
        var sample = Assert.Single(sampleRows.EnumerateArray().ToArray());
        Assert.Equal("Beyond3Sigma", sample.GetProperty("ruleCodes").GetString());
        Assert.Equal(20.0, sample.GetProperty("mean").GetDouble());

        var charts = await Json(await opA.GetAsync("/api/spc/charts"));
        var mine = charts.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == chartId);
        Assert.Contains("Beyond3Sigma", mine.GetProperty("activeRules").EnumerateArray().Select(x => x.GetString()));
        await using (var runtime = await Runtime().CreateDbContextAsync())
            Assert.True(await runtime.AlarmEvents.AnyAsync(x => x.TagId == _site.TagA && x.Message.Contains("SPC") && x.EndTs == null));

        cache.Set(new TagValue(_site.TagA, 10, TagQuality.Good, now + 86_400_000));
        await monitor.SampleOnceAsync(now + 86_400_000, default, forceReload: true);
        var recoveredChart = await Json(await opA.GetAsync($"/api/spc/charts/{chartId}"));
        Assert.Empty(recoveredChart.GetProperty("activeRules").EnumerateArray());
        var violations = await Json(await opA.GetAsync($"/api/spc/charts/{chartId}/violations?from={now - 1000}&to={now + 86_400_000 + 1000}"));
        var violation = Assert.Single(violations.EnumerateArray().ToArray());
        Assert.Equal("Beyond3Sigma", violation.GetProperty("rule").GetString());
        Assert.NotEqual(JsonValueKind.Null, violation.GetProperty("recoveredTs").ValueKind);
        await using (var runtime = await Runtime().CreateDbContextAsync())
            Assert.True(await runtime.AlarmEvents.AnyAsync(x => x.TagId == _site.TagA && x.Message.Contains("SPC") && x.EndTs != null));

        // 管理员在 B 区建图，opA 列表不可见。
        var bChart = await Json(await admin.PostAsJsonAsync("/api/spc/charts", ChartBody(_site.AreaB, _site.TagB, name + "-B2")));
        var bId = bChart.GetProperty("id").GetInt32();
        var visible = await Json(await opA.GetAsync("/api/spc/charts"));
        Assert.DoesNotContain(visible.EnumerateArray(), x => x.GetProperty("id").GetInt32() == bId);

        var revision = recoveredChart.GetProperty("revision").GetInt32();
        var update = new
        {
            name = name + "-edit", enabled = true, areaId = _site.AreaA, tagId = _site.TagA, chartType = "Individuals", subgroupSize = 1,
            samplingIntervalSec = 86400, baselineSubgroups = 5, autoLimits = false,
            centerLine = 10.0, upperControlLimit = 13.0, lowerControlLimit = 7.0,
            specUpperLimit = (double?)null, specLowerLimit = (double?)null,
            ruleBeyond3Sigma = true, ruleTwoOfThreeBeyond2Sigma = false, ruleFourOfFiveBeyond1Sigma = false,
            ruleEightOnOneSide = false, ruleRangeBeyondLimit = false, description = "", revision
        };
        Assert.Equal(HttpStatusCode.OK, (await cfgA.PutAsJsonAsync($"/api/spc/charts/{chartId}", update)).StatusCode);
        // 只改名称/规格/规则不切换采样代次，之前的历史样本仍应可查。
        var afterEdit = await Json(await opA.GetAsync(
            $"/api/spc/charts/{chartId}/samples?from={now - 1000}&to={now + 86_400_000 + 1000}"));
        Assert.Equal(2, afterEdit.GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await cfgA.PutAsJsonAsync($"/api/spc/charts/{chartId}", update)).StatusCode);
    }

    [Fact]
    public async Task AutoLimitsEstablishBeyondFortySamples()
    {
        var admin = await _site.ClientAsync("admin");
        var name = Unique("SPC-AUTO-");
        var body = new
        {
            name, enabled = true, areaId = _site.AreaB, tagId = _site.TagB, chartType = "Individuals", subgroupSize = 1,
            samplingIntervalSec = 86400, baselineSubgroups = 45, autoLimits = true,
            centerLine = (double?)null, upperControlLimit = (double?)null, lowerControlLimit = (double?)null,
            specUpperLimit = 100.0, specLowerLimit = 0.0,
            ruleBeyond3Sigma = true, ruleTwoOfThreeBeyond2Sigma = true, ruleFourOfFiveBeyond1Sigma = true,
            ruleEightOnOneSide = true, ruleRangeBeyondLimit = true, description = "", revision = 0
        };
        var created = await Json(await admin.PostAsJsonAsync("/api/spc/charts", body));
        var chartId = created.GetProperty("id").GetInt32();
        var cache = _site.Services.GetRequiredService<IRealtimeCache>();
        var monitor = _site.Services.GetRequiredService<SpcMonitorService>();
        var start = ScadaTime.NowMs();
        for (var i = 0; i < 46; i++)
        {
            var ts = start + i * 86_400_000L;
            cache.Set(new TagValue(_site.TagB, 50 + (i % 3) * 0.1, TagQuality.Good, ts));
            await monitor.SampleOnceAsync(ts, default, forceReload: true);
        }
        var view = await Json(await admin.GetAsync($"/api/spc/charts/{chartId}"));
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("limitsEstablishedTs").ValueKind);
        Assert.False(view.GetProperty("limitsProvisional").GetBoolean());
    }

}
