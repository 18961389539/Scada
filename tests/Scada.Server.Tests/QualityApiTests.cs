using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Scada.Core.Models;

namespace Scada.Server.Tests;

public sealed class QualityApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public QualityApiTests(AuthorizationTests.Site site) => _site = site;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public async Task QualityInspectionPermissionsIsolationAndSummary()
    {
        var opA = await _site.ClientAsync("opA");
        var cfgA = await _site.ClientAsync("cfgA");
        var admin = await _site.ClientAsync("admin");
        var defectCode = Unique("DEF-");
        Assert.Equal(HttpStatusCode.Forbidden, (await opA.PostAsJsonAsync("/api/quality/defect-codes",
            new { code = defectCode, name = "尺寸超差", category = "尺寸", description = "", enabled = true })).StatusCode);
        var code = await Json(await cfgA.PostAsJsonAsync("/api/quality/defect-codes",
            new { code = defectCode, name = "尺寸超差", category = "尺寸", description = "", enabled = true }));
        var codeId = code.GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Forbidden, (await cfgA.PostAsJsonAsync("/api/quality/inspections",
            new { sn = Unique("SN-"), batchNo = "B1", product = "P1", areaId = _site.AreaA, deviceId = _site.DeviceA, tagId = _site.TagA,
                result = "Failed", defectCodeId = codeId, inspector = "cfgA", ts = ScadaTime.NowMs(), note = "" })).StatusCode);
        var failed = await Json(await opA.PostAsJsonAsync("/api/quality/inspections",
            new { sn = Unique("SN-"), batchNo = "B1", product = "P1", areaId = _site.AreaA, deviceId = _site.DeviceA, tagId = _site.TagA,
                result = "Failed", defectCodeId = codeId, inspector = "opA", ts = ScadaTime.NowMs(), note = "" }));
        var failedId = failed.GetProperty("id").GetInt32();
        Assert.Equal("尺寸超差", failed.GetProperty("defectName").GetString());
        Assert.Equal("A温度", failed.GetProperty("tagName").GetString());

        var passed = await Json(await opA.PostAsJsonAsync("/api/quality/inspections",
            new { sn = Unique("SN-"), batchNo = "B1", product = "P1", areaId = _site.AreaA, deviceId = (int?)null, tagId = (int?)null,
                result = "Passed", defectCodeId = (int?)null, inspector = "opA", ts = ScadaTime.NowMs(), note = "" }));
        var passedId = passed.GetProperty("id").GetInt32();

        var now = ScadaTime.NowMs();
        var summary = await Json(await opA.GetAsync($"/api/quality/summary?from={now - 3600_000}&to={now + 3600_000}"));
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("passed").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
        Assert.Equal(0.5, summary.GetProperty("yield").GetDouble());
        var defectRows = summary.GetProperty("byDefect").EnumerateArray().ToArray();
        Assert.Single(defectRows);
        Assert.Equal(1, defectRows[0].GetProperty("count").GetInt32());

        var list = await Json(await opA.GetAsync($"/api/quality/inspections?page=1&pageSize=50&from={now - 3600_000}&to={now + 3600_000}"));
        Assert.Equal(2, list.GetProperty("total").GetInt32());

        var adminB = await admin.PostAsJsonAsync("/api/quality/inspections",
            new { sn = Unique("SN-B-"), batchNo = "B2", product = "P1", areaId = _site.AreaB, deviceId = (int?)null, tagId = (int?)null,
                result = "Passed", defectCodeId = (int?)null, inspector = "admin", ts = now, note = "" });
        Assert.Equal(HttpStatusCode.Created, adminB.StatusCode);
        var opList = await Json(await opA.GetAsync($"/api/quality/inspections?page=1&pageSize=50&from={now - 3600_000}&to={now + 3600_000}"));
        Assert.Equal(2, opList.GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.Forbidden, (await opA.PutAsJsonAsync($"/api/quality/inspections/{failedId}",
            new { sn = "X", batchNo = "B1", product = "P1", areaId = _site.AreaA, deviceId = (int?)null, tagId = (int?)null,
                result = "Failed", defectCodeId = codeId, inspector = "opA", ts = now, note = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cfgA.PutAsJsonAsync($"/api/quality/inspections/{failedId}",
            new { sn = "X", batchNo = "B1", product = "P1", areaId = _site.AreaA, deviceId = (int?)null, tagId = (int?)null,
                result = "Failed", defectCodeId = codeId, inspector = "cfgA", ts = now, note = "复核" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cfgA.DeleteAsync($"/api/quality/defect-codes/{codeId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await cfgA.DeleteAsync($"/api/quality/inspections/{failedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await cfgA.DeleteAsync($"/api/quality/inspections/{passedId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await cfgA.DeleteAsync($"/api/quality/defect-codes/{codeId}")).StatusCode);
    }
}
