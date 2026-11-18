using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core;
using Scada.Data;
using Scada.Engine.Maintenance;
using Scada.Server.Services;

namespace Scada.Server.Tests;

/// <summary>
/// Optional local-collection-off mode. Tests do not start Gateway.Host or field links.
/// </summary>
public sealed class LocalCollectionIsolationTests : IClassFixture<LocalCollectionIsolationTests.Site>
{
    public sealed class Site : McpApiTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder b)
        {
            base.ConfigureSettings(b);
            b.UseSetting("Scada:LocalCollectionEnabled", "false");
            b.UseSetting("Scada:ControlRequireConfirmByDefault", "false");
        }
    }

    private readonly Site _site;
    public LocalCollectionIsolationTests(Site site) => _site = site;

    [Fact]
    public async Task UnassignedEnabledDevicesAreRejectedAndSurfaceHealthWarnings()
    {
        using var admin = await _site.ClientAsync("admin");
        var enabled = new
        {
            name = "iso" + Guid.NewGuid().ToString("N")[..8],
            protocol = "ModbusTcp", host = "127.0.0.1", port = 1502, stationNumber = 1,
            byteOrder = "ABCD", pollIntervalMs = 1000, timeoutMs = 500, enabled = true, areaId = _site.AreaA
        };
        var refused = await admin.PostAsJsonAsync("/api/devices", enabled);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(LocalCollection.UnassignedDeviceMessage, await refused.Content.ReadAsStringAsync());

        var disabled = new
        {
            name = enabled.name + "off",
            protocol = "ModbusTcp", host = "127.0.0.1", port = 1502, stationNumber = 1,
            byteOrder = "ABCD", pollIntervalMs = 1000, timeoutMs = 500, enabled = false, areaId = _site.AreaA
        };
        var created = await admin.PostAsJsonAsync("/api/devices", disabled);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());

        var current = await (await admin.GetAsync($"/api/devices/{_site.DeviceA}")).Content.ReadFromJsonAsync<JsonElement>();
        var body = _site.DeviceBody(current, _site.AreaA);
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(body))!;
        payload["enabled"] = JsonSerializer.SerializeToElement(true);
        var updated = await admin.PutAsJsonAsync($"/api/devices/{_site.DeviceA}", payload);
        Assert.Equal(HttpStatusCode.BadRequest, updated.StatusCode);
        Assert.Contains(LocalCollection.UnassignedDeviceMessage, await updated.Content.ReadAsStringAsync());

        var watch = _site.Services.GetRequiredService<SystemWatch>().Evaluate();
        Assert.Contains(watch.Warnings, warning => warning.Code == LocalCollection.DisabledHealthCode);

        await using var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        var device = await db.Devices.SingleAsync(x => x.Id == _site.DeviceA);
        device.Enabled = true;
        await db.SaveChangesAsync();
        await _site.Services.GetRequiredService<ConfigReloader>().ReloadAsync();

        watch = _site.Services.GetRequiredService<SystemWatch>().Evaluate();
        Assert.Contains(watch.Warnings, warning => warning.Code == LocalCollection.UnassignedHealthCode);

        var write = await admin.PostAsJsonAsync("/api/commands", new { tagId = _site.TagA, value = 1, requestId = Guid.NewGuid(), confirmed = true });
        Assert.True(write.IsSuccessStatusCode, await write.Content.ReadAsStringAsync());
        var result = await write.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Contains(LocalCollection.DisabledMessage, result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnassignedReconnectExplainsThatLocalCollectionIsDisabled()
    {
        using var admin = await _site.ClientAsync("admin");
        await using var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        var device = await db.Devices.SingleAsync(x => x.Id == _site.DeviceA);
        device.Enabled = true;
        await db.SaveChangesAsync();
        await _site.Services.GetRequiredService<ConfigReloader>().ReloadAsync();

        var rest = await admin.PostAsync($"/api/devices/{_site.DeviceA}/diagnostics/reconnect", null);
        Assert.Equal(HttpStatusCode.Conflict, rest.StatusCode);
        var restBody = await rest.Content.ReadAsStringAsync();
        Assert.Contains(LocalCollection.DisabledMessage, restBody);
        Assert.DoesNotContain("配置已变化", restBody);

        using var mcp = _site.Server.CreateClient();
        var reconnect = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "reconnect_device", new { deviceId = _site.DeviceA }));
        Assert.False(reconnect.GetProperty("success").GetBoolean(), reconnect.ToString());
        Assert.Contains(LocalCollection.DisabledMessage, reconnect.GetProperty("message").GetString());
        Assert.DoesNotContain("configuration changed", reconnect.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnassignedDeviceTestsExplainThatLocalCollectionIsDisabled()
    {
        using var admin = await _site.ClientAsync("admin");

        var saved = await admin.PostAsJsonAsync($"/api/devices/{_site.DeviceA}/test", new { });
        Assert.Equal(HttpStatusCode.Conflict, saved.StatusCode);
        Assert.Contains(LocalCollection.DisabledMessage, await saved.Content.ReadAsStringAsync());

        var draft = await admin.PostAsJsonAsync("/api/devices/test-draft", new
        {
            id = _site.DeviceA,
            device = AuthorizationTests.Site.DeviceRequest("A区设备", _site.AreaA)
        });
        Assert.Equal(HttpStatusCode.Conflict, draft.StatusCode);
        Assert.Contains(LocalCollection.DisabledMessage, await draft.Content.ReadAsStringAsync());

        // 若仍从中心 ConnectAsync，该不可达地址会等到 TimeoutMs 才失败；409 应立即返回。
        var started = DateTime.UtcNow;
        var unsaved = await admin.PostAsJsonAsync("/api/devices/test-draft", new
        {
            device = new
            {
                name = "draft-probe",
                protocol = "ModbusTcp",
                host = "192.0.2.1",
                port = 1502,
                stationNumber = 1,
                byteOrder = "ABCD",
                pollIntervalMs = 1000,
                timeoutMs = 8000,
                enabled = false,
                areaId = _site.AreaA
            }
        });
        var elapsed = DateTime.UtcNow - started;
        Assert.Equal(HttpStatusCode.Conflict, unsaved.StatusCode);
        Assert.Contains(LocalCollection.DisabledMessage, await unsaved.Content.ReadAsStringAsync());
        Assert.True(elapsed.TotalSeconds < 2, $"试连仍可能走了本机建链：耗时 {elapsed.TotalMilliseconds:0} ms");
    }
}
