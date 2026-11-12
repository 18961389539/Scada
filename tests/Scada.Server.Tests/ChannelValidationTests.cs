using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Scada.Server.Tests;

/// <summary>
/// 通道（共享物理链路）的配置约束：端点由通道下发、同总线站号唯一、串口只能有一个归属。
/// 这些规则以前不存在，导致"一台网关带 10 个从站 = 10 条 TCP"和"同串口第二台设备永远离线"。
/// </summary>
public sealed class ChannelValidationTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public ChannelValidationTests(AuthorizationTests.Site site) => _site = site;

    // 同 DeviceValidationTests：串口号在同一个库里必须唯一，随机窄号段会撞车。
    private static int _nextPort = Random.Shared.Next(20000, 29000);
    private static string NextPort() => "COM" + Interlocked.Increment(ref _nextPort);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private static object Channel(string name, string protocol = "ModbusTcp", string host = "10.9.9.9", int port = 1502, string serialPort = "COM1") => new
    {
        name, protocol, description = "", host, port, serialPort,
        baudRate = 9600, dataBits = 8, parity = "None", stopBits = "One", timeoutMs = 1000, enabled = true
    };

    private object Device(string name, int? channelId, string protocol = "ModbusTcp", int station = 1,
        string host = "", int port = 0, string serialPort = "COM1") => new
    {
        name, protocol, host, port, stationNumber = station, byteOrder = "ABCD",
        pollIntervalMs = 1000, timeoutMs = 1000, enabled = true, areaId = _site.AreaA, serialPort, baudRate = 9600,
        channelId
    };

    private static async Task<string> MessageAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString() ?? "";

    private static async Task<int> CreateChannelAsync(HttpClient client, object payload)
    {
        var response = await client.PostAsJsonAsync("/api/channels", payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task DeviceInheritsEndpointFromChannel()
    {
        var admin = await _site.ClientAsync("admin");
        var host = $"10.9.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        var channelId = await CreateChannelAsync(admin, Channel(Unique("网关通道"), host: host, port: 1502));

        // 设备自身不带端点：通道是端点权威，保存时下发。
        var response = await admin.PostAsJsonAsync("/api/devices", Device(Unique("从站"), channelId, host: "", port: 0, station: 3));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var device = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(host, device.GetProperty("host").GetString());
        Assert.Equal(1502, device.GetProperty("port").GetInt32());
        Assert.Equal(channelId, device.GetProperty("channelId").GetInt32());
    }

    [Fact]
    public async Task DuplicateStationOnSameChannelIsRejected()
    {
        var admin = await _site.ClientAsync("admin");
        var channelId = await CreateChannelAsync(admin, Channel(Unique("站号通道")));

        var first = await admin.PostAsJsonAsync("/api/devices", Device(Unique("从站A"), channelId, station: 7));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        var second = await admin.PostAsJsonAsync("/api/devices", Device(Unique("从站B"), channelId, station: 7));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var message = await MessageAsync(second);
        Assert.Contains("站号", message);
    }

    [Fact]
    public async Task DeviceProtocolMustMatchChannel()
    {
        var admin = await _site.ClientAsync("admin");
        var channelId = await CreateChannelAsync(admin, Channel(Unique("协议通道")));

        var response = await admin.PostAsJsonAsync("/api/devices", Device(Unique("错协议"), channelId, protocol: "ModbusRtu", serialPort: "COM1"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("不一致", await MessageAsync(response));
    }

    [Fact]
    public async Task SerialSlavesShareOneChannel()
    {
        var admin = await _site.ClientAsync("admin");
        var port = NextPort();
        var channelId = await CreateChannelAsync(admin, Channel(Unique("485总线"), protocol: "ModbusRtu", host: "", port: 0, serialPort: port));

        var first = await admin.PostAsJsonAsync("/api/devices", Device(Unique("表1"), channelId, protocol: "ModbusRtu", station: 1, serialPort: port));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        var second = await admin.PostAsJsonAsync("/api/devices", Device(Unique("表2"), channelId, protocol: "ModbusRtu", station: 2, serialPort: port));
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChannelLessDeviceCannotTakeOverChannelSerialPort()
    {
        var admin = await _site.ClientAsync("admin");
        var port = NextPort();
        var channelId = await CreateChannelAsync(admin, Channel(Unique("独占校验"), protocol: "ModbusRtu", host: "", port: 0, serialPort: port));
        var onChannel = await admin.PostAsJsonAsync("/api/devices", Device(Unique("通道表"), channelId, protocol: "ModbusRtu", station: 1, serialPort: port));
        Assert.True(onChannel.IsSuccessStatusCode, await onChannel.Content.ReadAsStringAsync());

        // 未绑定通道的第二台设备会各开一次串口，必然有一台永久离线，必须提前拦住。
        var response = await admin.PostAsJsonAsync("/api/devices", Device(Unique("野设备"), null, protocol: "ModbusRtu", station: 2, serialPort: port));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("占用", await MessageAsync(response));
    }

    [Fact]
    public async Task ChannelCannotBeDeletedWhileDevicesAreBound()
    {
        var admin = await _site.ClientAsync("admin");
        var channelId = await CreateChannelAsync(admin, Channel(Unique("待删通道")));
        var device = await admin.PostAsJsonAsync("/api/devices", Device(Unique("绑定设备"), channelId, station: 1));
        Assert.True(device.IsSuccessStatusCode, await device.Content.ReadAsStringAsync());

        var response = await admin.DeleteAsync($"/api/channels/{channelId}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("解除绑定", await MessageAsync(response));
    }

    [Fact]
    public async Task NonModbusProtocolCannotBindChannel()
    {
        var admin = await _site.ClientAsync("admin");
        var channelId = await CreateChannelAsync(admin, Channel(Unique("误绑通道")));
        var payload = new
        {
            name = Unique("OPC设备"), protocol = "OpcUa", host = "127.0.0.1", port = 4840, stationNumber = 1,
            byteOrder = "ABCD", pollIntervalMs = 1000, timeoutMs = 1000, enabled = true, areaId = _site.AreaA,
            opcEndpointUrl = "opc.tcp://127.0.0.1:4840", channelId
        };
        var response = await admin.PostAsJsonAsync("/api/devices", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("共享物理通道", await MessageAsync(response));
    }
}
