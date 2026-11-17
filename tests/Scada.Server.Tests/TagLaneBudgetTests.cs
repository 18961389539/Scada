using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Data;

namespace Scada.Server.Tests;

/// <summary>
/// 采集档位预算按有效周期计算：未填周期跟随设备默认，改设备周期后必须重算。
/// </summary>
public sealed class TagLaneBudgetTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public TagLaneBudgetTests(AuthorizationTests.Site site) => _site = site;

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

    private static object DevicePut(JsonElement device, int pollIntervalMs) => new
    {
        name = device.GetProperty("name").GetString(),
        protocol = device.GetProperty("protocol").GetString(),
        host = device.GetProperty("host").GetString(),
        port = device.GetProperty("port").GetInt32(),
        stationNumber = device.GetProperty("stationNumber").GetInt32(),
        byteOrder = device.GetProperty("byteOrder").GetString(),
        pollIntervalMs,
        timeoutMs = device.GetProperty("timeoutMs").GetInt32(),
        enabled = device.GetProperty("enabled").GetBoolean(),
        areaId = device.GetProperty("areaId").GetInt32()
    };

    private async Task SeedRatesAsync(HttpClient admin, int deviceId, params int?[] rates)
    {
        for (var i = 0; i < rates.Length; i++)
        {
            var created = await admin.PostAsJsonAsync("/api/tags", TagBody(deviceId, "p" + i, i, rates[i]));
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task CreateRejectsFifthEffectiveLaneThatFollowsDeviceDefault()
    {
        var admin = await _site.ClientAsync("admin");
        var name = Unique("档位设备");
        var deviceId = await CreateDeviceAsync(admin, name);
        await SeedRatesAsync(admin, deviceId, 100, 200, 300, 400);

        var rejected = await admin.PostAsJsonAsync("/api/tags", TagBody(deviceId, "follow", 10, null));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains(name, body);
        Assert.Contains("1000ms（设备默认）", body);
        Assert.Contains("5 档", body);

        var merged = await admin.PostAsJsonAsync("/api/tags", TagBody(deviceId, "same400", 11, 400));
        Assert.True(merged.IsSuccessStatusCode, await merged.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreateAllowsFourEffectiveLanesIncludingDeviceDefault()
    {
        var admin = await _site.ClientAsync("admin");
        var deviceId = await CreateDeviceAsync(admin, Unique("四档设备"));
        await SeedRatesAsync(admin, deviceId, 100, 200, 300, null);
        await using var db = await Factory.CreateDbContextAsync();
        Assert.Equal(4, await db.Tags.CountAsync(x => x.DeviceId == deviceId));
        Assert.Null((await db.Tags.SingleAsync(x => x.DeviceId == deviceId && x.Name == "p3")).ScanRateMs);
    }

    [Fact]
    public async Task ChangingPollIntervalRecomputesEffectiveLanes()
    {
        var admin = await _site.ClientAsync("admin");
        var name = Unique("改周期设备");
        var deviceId = await CreateDeviceAsync(admin, name, 1000);
        // 100/200/300/1000 显式，再加一个跟随 1000 的测点：运行时仍是 4 档。
        await SeedRatesAsync(admin, deviceId, 100, 200, 300, 1000, null);

        var device = await admin.GetFromJsonAsync<JsonElement>($"/api/devices/{deviceId}");
        var rejected = await admin.PutAsJsonAsync($"/api/devices/{deviceId}", DevicePut(device, 500));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains(name, body);
        Assert.Contains("500ms（设备默认）", body);
        Assert.Contains("1000ms", body);
        Assert.Contains("5 档", body);

        await using (var db = await Factory.CreateDbContextAsync())
            Assert.Equal(1000, (await db.Devices.SingleAsync(x => x.Id == deviceId)).PollIntervalMs);

        // 把默认周期改到已有档位，跟随默认的测点合并进去，仍然是 4 档。
        var allowed = await admin.PutAsJsonAsync($"/api/devices/{deviceId}", DevicePut(device, 200));
        Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync());
        await using (var db = await Factory.CreateDbContextAsync())
            Assert.Equal(200, (await db.Devices.SingleAsync(x => x.Id == deviceId)).PollIntervalMs);
    }
}
