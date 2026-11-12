using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Scada.Server.Tests;

// 串口配置是全局资源：只有全局配置权限能改，增删改由审计拦截器自动记录。
public sealed class SerialProfileApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public SerialProfileApiTests(AuthorizationTests.Site site) => _site = site;

    [Fact]
    public async Task CrudIsGlobalConfigureAuditedAndRejectsDuplicateNames()
    {
        var admin = await _site.ClientAsync("admin");
        var cfg = await _site.ClientAsync("cfgA");
        var name = "serial-" + Guid.NewGuid().ToString("N")[..8];
        var port = "COM_TEST_" + Guid.NewGuid().ToString("N")[..6];
        var input = new
        {
            name, description = "test", enabled = false, port, baudRate = 9600, dataBits = 8,
            parity = "None", stopBits = "One", encoding = "utf-8", newLine = "\r\n",
            readTimeoutMs = 2000, maxReplyChars = 4096, dtrEnable = false, rtsEnable = false, revision = 0
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/serial-profiles", input)).StatusCode);

        var created = await admin.PostAsJsonAsync("/api/serial-profiles", input);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = item.GetProperty("id").GetInt32();
        Assert.True(item.GetProperty("editable").GetBoolean());
        Assert.Equal("database", item.GetProperty("source").GetString());
        var createdLog = await _site.LastLogAsync(x => x.TargetType == "SerialProfile" && x.TargetId == id.ToString() && x.Action.Contains(name));
        Assert.NotNull(createdLog);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/serial-profiles", input)).StatusCode);

        var update = new
        {
            name, description = "updated", enabled = false, port, baudRate = 19200, dataBits = 8,
            parity = "Even", stopBits = "One", encoding = "ascii", newLine = "\n",
            readTimeoutMs = 1500, maxReplyChars = 1024, dtrEnable = true, rtsEnable = false,
            revision = item.GetProperty("revision").GetInt64()
        };
        var updated = await admin.PutAsJsonAsync($"/api/serial-profiles/{id}", update);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedItem = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(19200, updatedItem.GetProperty("baudRate").GetInt32());
        Assert.Equal("ascii", updatedItem.GetProperty("encoding").GetString());
        Assert.NotNull(await _site.LastLogAsync(x => x.TargetType == "SerialProfile" && x.TargetId == id.ToString() && x.Action.StartsWith("修改")));

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/serial-profiles");
        Assert.Contains(list.GetProperty("items").EnumerateArray(), row => row.GetProperty("id").GetInt32() == id);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/serial-profiles/{id}")).StatusCode);
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/serial-profiles");
        Assert.DoesNotContain(after.GetProperty("items").EnumerateArray(), row => row.GetProperty("id").GetInt32() == id);
        Assert.NotNull(await _site.LastLogAsync(x => x.TargetType == "SerialProfile" && x.TargetId == id.ToString() && x.Action.StartsWith("删除")));
    }
}
