using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core;
using Scada.Data;

namespace Scada.Server.Tests;

/// <summary>
/// 工程导入必须走与 REST/MCP 同一套设备校验：本机采集隔离、同通道站号唯一、串口占用。
/// 合法的共享通道多从站仍应能导入。
/// </summary>
public sealed class ProjectDeviceImportTests : IClassFixture<AuthorizationTests.Site>, IClassFixture<LocalCollectionIsolationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    private readonly LocalCollectionIsolationTests.Site _isolated;

    public ProjectDeviceImportTests(AuthorizationTests.Site site, LocalCollectionIsolationTests.Site isolated)
    {
        _site = site;
        _isolated = isolated;
    }

    private IDbContextFactory<ConfigDbContext> Factory(AuthorizationTests.Site site) =>
        site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    // 与 DeviceValidationTests / ChannelValidationTests 错开号段，避免同一配置库里串口撞车。
    private static int _nextPort = Random.Shared.Next(40000, 49000);
    private static string NextPort() => "COM" + Interlocked.Increment(ref _nextPort);

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonObject> ExportAsync(HttpClient client) =>
        JsonNode.Parse(await (await client.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();

    private static async Task<JsonElement> PreviewAsync(HttpClient client, JsonObject package, string resolution = "Skip") =>
        await Ok(await client.PostAsJsonAsync("/api/project/import/preview", new { package, resolution }));

    private static JsonObject Item(JsonObject package, string section, string key) =>
        package["sections"]![section]!.AsArray().OfType<JsonObject>().Single(x => x["key"]!.GetValue<string>() == key);

    private static JsonObject CloneDevice(JsonObject package, string sourceKey, string newName)
    {
        var extra = (JsonObject)Item(package, "devices", sourceKey).DeepClone();
        extra["key"] = newName;
        extra["data"]!["name"] = newName;
        package["sections"]!["devices"]!.AsArray().Add(extra);
        return extra;
    }

    private object TcpDevice(string name, int areaId, int? channelId, int station) => new
    {
        name,
        protocol = "ModbusTcp",
        host = "",
        port = 0,
        stationNumber = station,
        byteOrder = "ABCD",
        pollIntervalMs = 1000,
        timeoutMs = 1000,
        enabled = true,
        areaId,
        channelId
    };

    private object RtuDevice(string name, int areaId, int? channelId, int station, string serialPort) => new
    {
        name,
        protocol = "ModbusRtu",
        host = "",
        port = 0,
        stationNumber = station,
        byteOrder = "ABCD",
        pollIntervalMs = 1000,
        timeoutMs = 1000,
        enabled = true,
        areaId,
        serialPort,
        baudRate = 9600,
        channelId
    };

    private static object Channel(string name, string protocol, string host, int port, string serialPort) => new
    {
        name,
        protocol,
        description = "",
        host,
        port,
        serialPort,
        baudRate = 9600,
        dataBits = 8,
        parity = "None",
        stopBits = "One",
        timeoutMs = 1000,
        enabled = true
    };

    private static async Task<int> CreateChannelAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/api/channels", payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task AssertPreviewAndImportRejectedAsync(HttpClient client, JsonObject package, string expected, string newDeviceName, IDbContextFactory<ConfigDbContext> factory)
    {
        var preview = await PreviewAsync(client, package);
        Assert.Contains(preview.GetProperty("errors").EnumerateArray(), x => x.GetString()!.Contains(expected));
        var failed = await client.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var body = await failed.Content.ReadAsStringAsync();
        Assert.Contains(expected, body);
        await using var db = await factory.CreateDbContextAsync();
        Assert.False(await db.Devices.AnyAsync(x => x.Name == newDeviceName));
    }

    [Fact]
    public async Task ImportRejectsEnabledUnassignedDeviceWhenLocalCollectionIsDisabled()
    {
        var admin = await _isolated.ClientAsync("admin");
        var package = await ExportAsync(admin);
        var name = Unique("隔离导入设备");
        var extra = CloneDevice(package, "A区设备", name);
        var isolated = extra["data"]!.AsObject();
        isolated["enabled"] = true;
        isolated.Remove("gatewayRef", out _);
        isolated["gatewayId"] = null;
        await AssertPreviewAndImportRejectedAsync(admin, package, LocalCollection.UnassignedDeviceMessage, name, Factory(_isolated));
    }

    [Fact]
    public async Task ImportRejectsDuplicateStationOnSameChannel()
    {
        var admin = await _site.ClientAsync("admin");
        var channelName = Unique("站号导入通道");
        var existingName = Unique("站号已有从站");
        var incomingName = Unique("站号冲突从站");
        var channelId = await CreateChannelAsync(admin, Channel(channelName, "ModbusTcp", "10.9.8.7", 1502, "COM1"));
        var first = await admin.PostAsJsonAsync("/api/devices", TcpDevice(existingName, _site.AreaA, channelId, 7));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        var package = await ExportAsync(admin);
        var extra = CloneDevice(package, existingName, incomingName);
        extra["data"]!["stationNumber"] = 7;
        extra["data"]!["acquisitionChannelRef"] = channelName;
        await AssertPreviewAndImportRejectedAsync(admin, package, "站号", incomingName, Factory(_site));
    }

    [Fact]
    public async Task ImportRejectsSerialPortOccupancyConflict()
    {
        var admin = await _site.ClientAsync("admin");
        var port = NextPort();
        var existingName = Unique("串口已有设备");
        var incomingName = Unique("串口冲突设备");
        var first = await admin.PostAsJsonAsync("/api/devices", RtuDevice(existingName, _site.AreaA, null, 1, port));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        var package = await ExportAsync(admin);
        var extra = CloneDevice(package, existingName, incomingName);
        var serial = extra["data"]!.AsObject();
        serial["serialPort"] = port;
        serial.Remove("acquisitionChannelRef", out _);
        serial["channelId"] = null;
        await AssertPreviewAndImportRejectedAsync(admin, package, "占用", incomingName, Factory(_site));
    }

    [Fact]
    public async Task ImportAllowsSharedChannelMultiSlaveDevices()
    {
        var admin = await _site.ClientAsync("admin");
        var port = NextPort();
        var channelName = Unique("共享总线");
        var slave1 = Unique("从站一");
        var slave2 = Unique("从站二");
        var channelId = await CreateChannelAsync(admin, Channel(channelName, "ModbusRtu", "", 0, port));
        var first = await admin.PostAsJsonAsync("/api/devices", RtuDevice(slave1, _site.AreaA, channelId, 1, port));
        var second = await admin.PostAsJsonAsync("/api/devices", RtuDevice(slave2, _site.AreaA, channelId, 2, port));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());

        var package = await ExportAsync(admin);
        var target = new AuthorizationTests.Site();
        await target.InitializeAsync();
        try
        {
            var other = await target.ClientAsync("admin");
            var preview = await PreviewAsync(other, package);
            Assert.Empty(preview.GetProperty("errors").EnumerateArray());
            var applied = await Ok(await other.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() }));
            Assert.True(applied.GetProperty("versionId").GetInt32() > 0);

            await using var db = await Factory(target).CreateDbContextAsync();
            var one = await db.Devices.SingleAsync(x => x.Name == slave1);
            var two = await db.Devices.SingleAsync(x => x.Name == slave2);
            var channel = await db.Channels.SingleAsync(x => x.Name == channelName);
            Assert.Equal(1, one.StationNumber);
            Assert.Equal(2, two.StationNumber);
            Assert.Equal(channel.Id, one.ChannelId);
            Assert.Equal(channel.Id, two.ChannelId);
            Assert.Equal(port, one.SerialPort);
            Assert.Equal(port, two.SerialPort);
        }
        finally
        {
            await target.DisposeAsync();
        }
    }
}
