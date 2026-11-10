using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Scada.Server.Tests;

// 设备配置的确定性约束：串口是独占资源、未实现协议不得启用采集。
// 这两条以前只在前端或运行时表现，配置层会先被写进库，现场表现为"一台设备一直离线"。
public sealed class DeviceValidationTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public DeviceValidationTests(AuthorizationTests.Site site) => _site = site;

    private object Rtu(string name, string port, bool enabled = true) => new
    {
        name, protocol = "ModbusRtu", host = "", port = 0, stationNumber = 1, byteOrder = "ABCD",
        pollIntervalMs = 1000, timeoutMs = 500, enabled, areaId = _site.AreaA,
        serialPort = port, baudRate = 9600
    };

    private object Iec61850(string name, bool enabled) => new
    {
        name, protocol = "Iec61850", host = "127.0.0.1", port = 102, stationNumber = 1, byteOrder = "ABCD",
        pollIntervalMs = 1000, timeoutMs = 500, enabled, areaId = _site.AreaA, protocolOptions = "{}"
    };

    private static async Task<string> MessageAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString() ?? "";

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public async Task SecondEnabledDeviceOnSameSerialPortIsRejected()
    {
        var admin = await _site.ClientAsync("admin");
        var port = "COM" + Random.Shared.Next(100, 250);
        var first = await admin.PostAsJsonAsync("/api/devices", Rtu(Unique("串口独占"), port));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        var second = await admin.PostAsJsonAsync("/api/devices", Rtu(Unique("串口独占"), port));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var message = await MessageAsync(second);
        Assert.Contains("占用", message);
        Assert.Contains(port, message);
    }

    [Fact]
    public async Task DisabledDeviceDoesNotOccupySerialPort()
    {
        var admin = await _site.ClientAsync("admin");
        var port = "COM" + Random.Shared.Next(100, 250);
        var enabled = await admin.PostAsJsonAsync("/api/devices", Rtu(Unique("串口启用"), port));
        Assert.True(enabled.IsSuccessStatusCode, await enabled.Content.ReadAsStringAsync());

        // 备用设备可以先配好、保持禁用，不占用串口。
        var disabled = await admin.PostAsJsonAsync("/api/devices", Rtu(Unique("串口备用"), port, enabled: false));
        Assert.True(disabled.IsSuccessStatusCode, await disabled.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Iec61850CannotBeEnabled()
    {
        var admin = await _site.ClientAsync("admin");
        var response = await admin.PostAsJsonAsync("/api/devices", Iec61850(Unique("61850启用"), enabled: true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("IEC 61850", await MessageAsync(response));
    }

    [Fact]
    public async Task Iec61850CanBeSavedForPreconfiguration()
    {
        var admin = await _site.ClientAsync("admin");
        var response = await admin.PostAsJsonAsync("/api/devices", Iec61850(Unique("61850预配置"), enabled: false));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}
