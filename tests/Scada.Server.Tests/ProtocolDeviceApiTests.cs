using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Data;

namespace Scada.Server.Tests;

// 文本地址协议：设备参数 JSON 与地址校验经 API 生效，并完整进入工程包导出/导入。
public sealed class ProtocolDeviceApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public ProtocolDeviceApiTests(AuthorizationTests.Site site) => _site = site;

    public static TheoryData<string, int, string, string, string> Protocols => new()
    {
        // protocol, port, options, valid address, invalid address
        { "Mqtt", 1883, "{\"qos\":1}", "plant/a|$.v;w=plant/a/set", "plant/#" },
        { "MitsubishiMc", 5000, "{\"format\":\"Ascii\",\"transport\":\"Udp\"}", "D100", "Q100" },
        { "OmronFins", 9600, "{\"transport\":\"Udp\",\"destNode\":10,\"sourceNode\":20}", "D100", "CIO10.16" },
        { "Iec104", 2404, "{\"commonAddress\":7,\"commandMode\":\"Direct\"}", "M_ME_NC:3001;cmd=C_SE_NC:6001", "M_ME_NC:3001" },
        { "BacnetIp", 47808, "{\"deviceInstance\":1234,\"useCov\":true}", "AV:10;pri=12", "AV:10;pri=6" },
        { "OpcDa", 0, "{\"progId\":\"Kepware.KEPServerEX.V6\"}", "Channel1.Device1.Tag1", " Channel1" },
        { "Iec61850", 102, "{}", "IED1LD0/GGIO1.SP.SPCSO1.setVal", "IED1LD0/GGIO1.XX.SPCSO1" },
        { "SparkplugB", 1883, "{\"groupId\":\"G\",\"edgeNodeId\":\"N\",\"deviceId\":\"D\"}", "Inputs/Temp", " bad" },
    };

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private object Device(string name, string protocol, int port, string options) => new
    {
        name, protocol, host = "127.0.0.1", port, stationNumber = 1, byteOrder = "ABCD", pollIntervalMs = 1000, timeoutMs = 500, enabled = false,
        areaId = _site.AreaA, protocolOptions = options
    };

    private static object TagBody(int deviceId, string name, string address) => new
    {
        deviceId, name, area = "Address", nodeId = address, address = 0, dataType = "Float", scale = 1.0, offset = 0.0, writable = true, archiveMode = "None", deadband = 0.0, archivePeriodSec = 600
    };

    private static object BatchTag(string name, int address) => new
    {
        name, area = "HoldingRegister", address, dataType = "Float", scale = 1.0, offset = 0.0, writable = false, archiveMode = "OnChange", deadband = 0.0, archivePeriodSec = 600
    };

    [Fact]
    public async Task BatchTagCreationIsAtomicAndRejectsInvalidRows()
    {
        var admin = await _site.ClientAsync("admin");
        var u = Guid.NewGuid().ToString("N")[..5];
        var deviceId = (await Ok(await admin.PostAsJsonAsync("/api/devices", Device("批量设备" + u, "ModbusTcp", 1502, "")))).GetProperty("id").GetInt32();

        var created = await Ok(await admin.PostAsJsonAsync("/api/tags/batch", new { deviceId, tags = new[] { BatchTag("批" + u + "-1", 10), BatchTag("批" + u + "-2", 11), BatchTag("批" + u + "-3", 12) } }));
        Assert.Equal(3, created.GetProperty("created").GetInt32());
        var tags = await admin.GetFromJsonAsync<JsonElement[]>($"/api/tags?deviceId={deviceId}");
        Assert.Equal(3, tags!.Length);

        // 重名和非法地址都必须整体拒绝，不能留下部分测点。
        var duplicate = await admin.PostAsJsonAsync("/api/tags/batch", new { deviceId, tags = new[] { BatchTag("批" + u + "-4", 20), BatchTag("批" + u + "-4", 21) } });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var invalid = await admin.PostAsJsonAsync("/api/tags/batch", new { deviceId, tags = new[] { BatchTag("批" + u + "-5", 30), BatchTag("批" + u + "-6", 70000) } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(3, (await admin.GetFromJsonAsync<JsonElement[]>($"/api/tags?deviceId={deviceId}"))!.Length);
    }

    [Fact]
    public async Task DraftDeviceTestConnectsWithFormValuesAndRequiresControlPermission()
    {
        var admin = await _site.ClientAsync("admin");
        var u = Guid.NewGuid().ToString("N")[..5];
        object Draft(string name) => new
        {
            name, protocol = "ModbusTcp", host = "127.0.0.1", port = 1, stationNumber = 1, byteOrder = "ABCD",
            pollIntervalMs = 1000, timeoutMs = 300, enabled = false, areaId = _site.AreaA, protocolOptions = ""
        };

        var response = await admin.PostAsJsonAsync("/api/devices/test-draft", new { id = (int?)null, device = Draft("试连" + u) });
        var result = await Ok(response);
        Assert.False(result.GetProperty("connected").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("message").GetString()));
        // 试连不落库。
        var devices = await admin.GetFromJsonAsync<JsonElement[]>("/api/devices");
        Assert.DoesNotContain(devices!, x => x.GetProperty("name").GetString() == "试连" + u);

        // 只有配置权、没有控制权的角色不能借用试连接口探测任意地址。
        var configureOnly = await _site.ClientAsync("cfgA");
        Assert.Equal(HttpStatusCode.Forbidden, (await configureOnly.PostAsJsonAsync("/api/devices/test-draft", new { id = (int?)null, device = Draft("越权" + u) })).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task DeviceAndTagValidationAndProjectRoundTrip(string protocol, int port, string options, string address, string invalid)
    {
        var admin = await _site.ClientAsync("admin");
        var u = Guid.NewGuid().ToString("N")[..5];
        var bad = await admin.PostAsJsonAsync("/api/devices", Device("坏参数" + u, protocol, port, "{\"nope\":1}"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var id = (await Ok(await admin.PostAsJsonAsync("/api/devices", Device(protocol + u, protocol, port, options)))).GetProperty("id").GetInt32();
        var badTag = await admin.PostAsJsonAsync("/api/tags", TagBody(id, "坏地址" + u, invalid));
        Assert.Equal(HttpStatusCode.BadRequest, badTag.StatusCode);
        var wrongArea = await admin.PostAsJsonAsync("/api/tags", new { deviceId = id, name = "错区" + u, area = "HoldingRegister", address = 0, dataType = "Float", scale = 1.0, offset = 0.0, archiveMode = "None", deadband = 0.0, archivePeriodSec = 600 });
        Assert.Equal(HttpStatusCode.BadRequest, wrongArea.StatusCode);
        await Ok(await admin.PostAsJsonAsync("/api/tags", TagBody(id, "点" + u, address)));

        var package = JsonNode.Parse(await (await admin.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();
        var device = package["sections"]!["devices"]!.AsArray().OfType<JsonObject>().Single(x => x["key"]!.GetValue<string>() == protocol + u);
        Assert.Equal(options, device["data"]!["protocolOptions"]!.GetValue<string>());

        var target = new AuthorizationTests.Site(); await target.InitializeAsync();
        try
        {
            var other = await target.ClientAsync("admin");
            var preview = await Ok(await other.PostAsJsonAsync("/api/project/import/preview", new { package, resolution = "Skip" }));
            Assert.Empty(preview.GetProperty("errors").EnumerateArray());
            await Ok(await other.PostAsJsonAsync("/api/project/import", new { package, resolution = "Skip", baseSha = preview.GetProperty("baseSha").GetString() }));
            await using var db = await target.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
            var imported = await db.Devices.SingleAsync(x => x.Name == protocol + u);
            Assert.Equal(options, imported.ProtocolOptions);
            Assert.Equal(address, (await db.Tags.SingleAsync(x => x.DeviceId == imported.Id)).NodeId);
        }
        finally { await target.DisposeAsync(); }
    }
}
