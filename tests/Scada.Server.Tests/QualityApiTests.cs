using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Engine.Calculations;
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

    [Fact]
    public async Task DailySummaryUsesShiftTimeZoneAroundMidnight()
    {
        var admin = await _site.ClientAsync("admin");
        var settings = _site.Services.GetRequiredService<IOptions<ScadaSettings>>().Value;
        var original = settings.ShiftTimeZone;
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        var zoneId = Math.Abs(localOffset.TotalHours) >= 2
            ? "UTC"
            : (OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai");
        try
        {
            settings.ShiftTimeZone = zoneId;
            var zone = CalcJobEngine.ResolveZone(zoneId);
            Assert.NotEqual(TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow), zone.GetUtcOffset(DateTime.UtcNow));

            var midnight = ZoneTime.DayStart(ScadaTime.NowMs() - 3L * 86_400_000, zone);
            var before = midnight - 60_000;
            var after = midnight + 60_000;
            var product = Unique("TZ-");
            var defect = await Json(await admin.PostAsJsonAsync("/api/quality/defect-codes",
                new { code = Unique("TZC-"), name = "时区试验", category = "试验", description = "", enabled = true }));
            var defectId = defect.GetProperty("id").GetInt32();
            await Json(await admin.PostAsJsonAsync("/api/quality/inspections",
                new { sn = Unique("SN-"), batchNo = "TZ", product, areaId = _site.AreaA, deviceId = (int?)null, tagId = (int?)null,
                    result = "Passed", defectCodeId = (int?)null, inspector = "admin", ts = before, note = "" }));
            await Json(await admin.PostAsJsonAsync("/api/quality/inspections",
                new { sn = Unique("SN-"), batchNo = "TZ", product, areaId = _site.AreaA, deviceId = (int?)null, tagId = (int?)null,
                    result = "Failed", defectCodeId = defectId, inspector = "admin", ts = after, note = "" }));

            var summary = await Json(await admin.GetAsync($"/api/quality/summary?product={product}&from={before - 1000}&to={after + 1000}"));
            var days = summary.GetProperty("byDay").EnumerateArray().Select(x => x.GetProperty("date").GetString()).ToArray();
            var expectedBefore = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(before), zone).ToString("yyyy-MM-dd");
            var expectedAfter = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(after), zone).ToString("yyyy-MM-dd");
            Assert.Equal(new[] { expectedBefore, expectedAfter }, days);
            Assert.NotEqual(expectedBefore, expectedAfter);
        }
        finally
        {
            settings.ShiftTimeZone = original;
        }
    }
    [Fact]
    public async Task ShiftTimeZoneCanBeReadAndUpdated()
    {
        var admin = await _site.ClientAsync("admin");
        var opA = await _site.ClientAsync("opA");
        var settings = _site.Services.GetRequiredService<IOptions<ScadaSettings>>().Value;
        var original = settings.ShiftTimeZone;
        try
        {
            var before = await Json(await admin.GetAsync("/api/oee/shift-timezone"));
            Assert.True(before.TryGetProperty("effectiveId", out _));
            Assert.True(before.TryGetProperty("usesServerLocal", out _));

            var zone = TimeZoneInfo.GetSystemTimeZones().Select(x => x.Id)
                .FirstOrDefault(id => id is "UTC" or "Asia/Shanghai" or "China Standard Time") ?? "UTC";
            var updated = await Json(await admin.PutAsJsonAsync("/api/oee/shift-timezone", new { timeZone = zone }));
            Assert.Equal(zone, updated.GetProperty("configured").GetString());
            Assert.False(updated.GetProperty("usesServerLocal").GetBoolean());
            Assert.Equal(zone, settings.ShiftTimeZone);

            Assert.Equal(HttpStatusCode.Forbidden, (await opA.PutAsJsonAsync("/api/oee/shift-timezone", new { timeZone = zone })).StatusCode);

            var cleared = await Json(await admin.PutAsJsonAsync("/api/oee/shift-timezone", new { timeZone = (string?)null }));
            Assert.True(cleared.GetProperty("usesServerLocal").GetBoolean());
            Assert.True(string.IsNullOrWhiteSpace(settings.ShiftTimeZone));
        }
        finally
        {
            settings.ShiftTimeZone = original;
        }
    }

    [Fact]
    public async Task InspectionRevisionAndExportIncludeAuditFields()
    {
        var opA = await _site.ClientAsync("opA");
        var cfgA = await _site.ClientAsync("cfgA");
        var defectCode = Unique("REV-");
        var code = await Json(await cfgA.PostAsJsonAsync("/api/quality/defect-codes",
            new { code = defectCode, name = "修订痕迹", category = "试验", description = "", enabled = true }));
        var codeId = code.GetProperty("id").GetInt32();
        var product = Unique("REV-P-");
        var created = await Json(await opA.PostAsJsonAsync("/api/quality/inspections",
            new { sn = Unique("SN-"), batchNo = "RB", product, areaId = _site.AreaA, deviceId = (int?)null, tagId = (int?)null,
                result = "Failed", defectCodeId = codeId, inspector = "opA", ts = ScadaTime.NowMs(), note = "" }));
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal("opA", created.GetProperty("createdBy").GetString());
        Assert.True(created.GetProperty("createdTs").GetInt64() > 0);
        Assert.Equal(created.GetProperty("createdTs").GetInt64(), created.GetProperty("updatedTs").GetInt64());
        Assert.Equal("", created.GetProperty("updatedBy").GetString());

        await Task.Delay(20);
        var updated = await Json(await cfgA.PutAsJsonAsync($"/api/quality/inspections/{id}",
            new { sn = created.GetProperty("sn").GetString(), batchNo = "RB", product, areaId = _site.AreaA, deviceId = (int?)null, tagId = (int?)null,
                result = "Failed", defectCodeId = codeId, inspector = "cfgA", ts = created.GetProperty("ts").GetInt64(), note = "已复核" }));
        Assert.Equal("cfgA", updated.GetProperty("updatedBy").GetString());
        Assert.True(updated.GetProperty("updatedTs").GetInt64() > updated.GetProperty("createdTs").GetInt64());

        var now = ScadaTime.NowMs();
        var export = await cfgA.GetAsync($"/api/quality/export?format=xlsx&product={product}&from={now - 3600_000}&to={now + 3600_000}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType?.MediaType);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 32);
        Assert.Equal(0x50, bytes[0]); // 'P' of PK zip/xlsx
        Assert.Equal(0x4B, bytes[1]);

        Assert.Equal(HttpStatusCode.NoContent, (await cfgA.DeleteAsync($"/api/quality/inspections/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await cfgA.DeleteAsync($"/api/quality/defect-codes/{codeId}")).StatusCode);
    }
}
