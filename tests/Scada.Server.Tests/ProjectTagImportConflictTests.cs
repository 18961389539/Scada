using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Data;

namespace Scada.Server.Tests;

/// <summary>
/// 工程导入测点走与批量建点相同的地址重叠检查，并对 OPC 文本地址拒绝同设备重复 NodeId。
/// </summary>
public sealed class ProjectTagImportConflictTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public ProjectTagImportConflictTests(AuthorizationTests.Site site) => _site = site;

    private IDbContextFactory<ConfigDbContext> Factory =>
        _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private async Task<int> CreateDeviceAsync(HttpClient admin, string name, string protocol = "ModbusTcp", int? port = null)
    {
        var response = await admin.PostAsJsonAsync("/api/devices", new
        {
            name,
            protocol,
            host = "127.0.0.1",
            port = port ?? Random.Shared.Next(20000, 50000),
            stationNumber = 1,
            byteOrder = "ABCD",
            pollIntervalMs = 1000,
            timeoutMs = 500,
            enabled = false,
            areaId = _site.AreaA
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static object RegisterTag(int deviceId, string name, int address, bool writable = true) => new
    {
        deviceId,
        name,
        area = "HoldingRegister",
        address,
        dataType = "Float",
        scale = 1.0,
        offset = 0.0,
        writable,
        archiveMode = "None",
        deadband = 0.0,
        archivePeriodSec = 600
    };

    private static object OpcTag(int deviceId, string name, string nodeId, bool writable = false) => new
    {
        deviceId,
        name,
        area = "OpcNode",
        address = 0,
        nodeId,
        dataType = "Float",
        scale = 1.0,
        offset = 0.0,
        writable,
        archiveMode = "None",
        deadband = 0.0,
        archivePeriodSec = 600
    };

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonObject> ExportAsync(HttpClient client) =>
        JsonNode.Parse(await (await client.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();

    private static JsonObject Item(JsonObject package, string section, string key) =>
        package["sections"]![section]!.AsArray().OfType<JsonObject>().Single(x => x["key"]!.GetValue<string>() == key);

    [Fact]
    public async Task ImportRejectsPartialAddressOverlapAndLeavesExistingTagsUnchanged()
    {
        var admin = await _site.ClientAsync("admin");
        var name = Unique("导入重叠");
        var deviceId = await CreateDeviceAsync(admin, name);
        Assert.True((await admin.PostAsJsonAsync("/api/tags", RegisterTag(deviceId, "p0", 10))).IsSuccessStatusCode);

        var package = await ExportAsync(admin);
        var extra = (JsonObject)Item(package, "tags", name + "/p0").DeepClone();
        extra["key"] = name + "/overlap";
        extra["data"]!["name"] = "overlap";
        extra["data"]!["address"] = 11;
        extra["data"]!["writable"] = true;
        package["sections"]!["tags"]!.AsArray().Add(extra);

        var preview = await Ok(await admin.PostAsJsonAsync("/api/project/import/preview", new { package, resolution = "Skip" }));
        Assert.Contains(preview.GetProperty("errors").EnumerateArray(), x => x.GetString()!.Contains("冲突") || x.GetString()!.Contains("重叠"));
        var failed = await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Tags.CountAsync(x => x.DeviceId == deviceId));
        Assert.False(await db.Tags.AnyAsync(x => x.DeviceId == deviceId && x.Name == "overlap"));
    }

    [Fact]
    public async Task ImportAllowsReadOnlySameSpanAlias()
    {
        var admin = await _site.ClientAsync("admin");
        var name = Unique("导入别名");
        var deviceId = await CreateDeviceAsync(admin, name);
        Assert.True((await admin.PostAsJsonAsync("/api/tags", RegisterTag(deviceId, "p0", 10, writable: true))).IsSuccessStatusCode);

        var package = await ExportAsync(admin);
        var extra = (JsonObject)Item(package, "tags", name + "/p0").DeepClone();
        extra["key"] = name + "/alias";
        extra["data"]!["name"] = "alias";
        extra["data"]!["address"] = 10;
        extra["data"]!["writable"] = false;
        package["sections"]!["tags"]!.AsArray().Add(extra);

        var preview = await Ok(await admin.PostAsJsonAsync("/api/project/import/preview", new { package, resolution = "Skip" }));
        Assert.DoesNotContain(preview.GetProperty("errors").EnumerateArray(), x => (x.GetString() ?? "").Contains("冲突") || (x.GetString() ?? "").Contains("重叠"));
        await Ok(await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() }));

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.Tags.CountAsync(x => x.DeviceId == deviceId));
        Assert.False((await db.Tags.SingleAsync(x => x.DeviceId == deviceId && x.Name == "alias")).Writable);
    }

    [Fact]
    public async Task ImportRejectsDuplicateOpcNodeIdOnSameDevice()
    {
        var admin = await _site.ClientAsync("admin");
        var name = Unique("导入节点");
        var deviceId = await CreateDeviceAsync(admin, name, "OpcUa", 4840);
        var created = await admin.PostAsJsonAsync("/api/tags", OpcTag(deviceId, "n0", "ns=2;s=Temperature"));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        var package = await ExportAsync(admin);
        var extra = (JsonObject)Item(package, "tags", name + "/n0").DeepClone();
        extra["key"] = name + "/dup";
        extra["data"]!["name"] = "dup";
        extra["data"]!["nodeId"] = "ns=2;s=Temperature";
        package["sections"]!["tags"]!.AsArray().Add(extra);

        var preview = await Ok(await admin.PostAsJsonAsync("/api/project/import/preview", new { package, resolution = "Skip" }));
        Assert.Contains(preview.GetProperty("errors").EnumerateArray(), x => x.GetString()!.Contains("相同 OPC 变量"));
        var failed = await admin.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Tags.CountAsync(x => x.DeviceId == deviceId));
        Assert.False(await db.Tags.AnyAsync(x => x.DeviceId == deviceId && x.Name == "dup"));
    }
}
