using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Data;

namespace Scada.Server.Tests;

/// <summary>
/// 工程导入与网页/MCP 使用同一套有效周期档位预算，超限出现在预览中且不会入库。
/// </summary>
public sealed class ProjectTagLaneBudgetTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public ProjectTagLaneBudgetTests(AuthorizationTests.Site site) => _site = site;

    private IDbContextFactory<ConfigDbContext> Factory =>
        _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private async Task<int> CreateDeviceAsync(HttpClient admin, string name, int pollIntervalMs = 1000)
    {
        var response = await admin.PostAsJsonAsync("/api/devices", new
        {
            name,
            protocol = "ModbusTcp",
            host = "127.0.0.1",
            port = Random.Shared.Next(20000, 50000),
            stationNumber = 1,
            byteOrder = "ABCD",
            pollIntervalMs,
            timeoutMs = 500,
            enabled = false,
            areaId = _site.AreaA
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static object TagBody(int deviceId, string name, int address, int? scanRateMs) => new
    {
        deviceId,
        name,
        area = "HoldingRegister",
        address,
        dataType = "Float",
        scale = 1.0,
        offset = 0.0,
        writable = true,
        archiveMode = "None",
        deadband = 0.0,
        archivePeriodSec = 600,
        scanRateMs
    };

    private async Task SeedRatesAsync(HttpClient admin, int deviceId, params int?[] rates)
    {
        for (var i = 0; i < rates.Length; i++)
        {
            var created = await admin.PostAsJsonAsync("/api/tags", TagBody(deviceId, "p" + i, i, rates[i]));
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        }
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonObject> ExportAsync(HttpClient client) =>
        JsonNode.Parse(await (await client.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();

    private static async Task<JsonElement> PreviewAsync(HttpClient client, JsonObject package) =>
        await Ok(await client.PostAsJsonAsync("/api/project/import/preview", new { package, resolution = "Skip" }));

    private static JsonObject Item(JsonObject package, string section, string key) =>
        package["sections"]![section]!.AsArray().OfType<JsonObject>().Single(x => x["key"]!.GetValue<string>() == key);

    [Fact]
    public async Task ImportRejectsFifthEffectiveLaneAndLeavesExistingTagsUnchanged()
    {
        var admin = await _site.ClientAsync("admin");
        var name = Unique("导入五档");
        var deviceId = await CreateDeviceAsync(admin, name);
        await SeedRatesAsync(admin, deviceId, 100, 200, 300, 400);

        var package = await ExportAsync(admin);
        var extra = (JsonObject)Item(package, "tags", name + "/p0").DeepClone();
        extra["key"] = name + "/follow";
        extra["data"]!["name"] = "follow";
        extra["data"]!["address"] = 99;
        extra["data"]!["scanRateMs"] = null;
        package["sections"]!["tags"]!.AsArray().Add(extra);

        var preview = await PreviewAsync(admin, package);
        Assert.Contains(preview.GetProperty("errors").EnumerateArray(), x =>
            x.GetString()!.Contains(name) && x.GetString()!.Contains("1000ms（设备默认）") && x.GetString()!.Contains("5 档"));
        var failed = await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Contains("5 档", await failed.Content.ReadAsStringAsync());

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(4, await db.Tags.CountAsync(x => x.DeviceId == deviceId));
        Assert.False(await db.Tags.AnyAsync(x => x.DeviceId == deviceId && x.Name == "follow"));
    }

    [Fact]
    public async Task ImportAllowsFourEffectiveLanesIncludingDeviceDefault()
    {
        var admin = await _site.ClientAsync("admin");
        var name = Unique("导入四档");
        var deviceId = await CreateDeviceAsync(admin, name);
        await SeedRatesAsync(admin, deviceId, 100, 200, 300);

        var package = await ExportAsync(admin);
        var extra = (JsonObject)Item(package, "tags", name + "/p0").DeepClone();
        extra["key"] = name + "/follow";
        extra["data"]!["name"] = "follow";
        extra["data"]!["address"] = 99;
        extra["data"]!["scanRateMs"] = null;
        package["sections"]!["tags"]!.AsArray().Add(extra);

        var preview = await PreviewAsync(admin, package);
        Assert.DoesNotContain(preview.GetProperty("errors").EnumerateArray(), x => x.GetString()!.Contains(name) && x.GetString()!.Contains("档位"));
        var applied = await Ok(await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() }));
        Assert.True(applied.GetProperty("versionId").GetInt32() > 0);

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(4, await db.Tags.CountAsync(x => x.DeviceId == deviceId));
        Assert.Null((await db.Tags.SingleAsync(x => x.DeviceId == deviceId && x.Name == "follow")).ScanRateMs);
    }

    [Fact]
    public async Task ImportRejectsPollIntervalChangeThatSplitsDefaultLane()
    {
        var admin = await _site.ClientAsync("admin");
        var name = Unique("导入改周期");
        var deviceId = await CreateDeviceAsync(admin, name, 1000);
        await SeedRatesAsync(admin, deviceId, 100, 200, 300, 1000, null);

        var package = await ExportAsync(admin);
        Item(package, "devices", name)["data"]!["pollIntervalMs"] = 500;

        // 本机已有同名设备时，Skip 会保留现场周期；要用 Overwrite 才会套用工程包里的新默认周期。
        var preview = await Ok(await admin.PostAsJsonAsync("/api/project/import/preview", new { package, resolution = "Overwrite" }));
        Assert.Contains(preview.GetProperty("errors").EnumerateArray(), x =>
            x.GetString()!.Contains(name) && x.GetString()!.Contains("500ms（设备默认）") && x.GetString()!.Contains("5 档"));
        var failed = await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Overwrite", baseSha = preview.GetProperty("baseSha").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1000, (await db.Devices.SingleAsync(x => x.Id == deviceId)).PollIntervalMs);
        Assert.Equal(5, await db.Tags.CountAsync(x => x.DeviceId == deviceId));
    }
}
