using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Scada.Server.Tests;

// 运行布局：多显示器区域校验、工作站分配与解析、只读工作站拒绝控制、画面删除保护、工程包。
public sealed class HmiLayoutTests : IClassFixture<ControlTests.ControlSite>
{
    private readonly ControlTests.ControlSite _site;
    public HmiLayoutTests(ControlTests.ControlSite site) => _site = site;
    private static string U() => Guid.NewGuid().ToString("N")[..5];
    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode is HttpStatusCode.NoContent ? default : await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task LayoutsWorkstationsAndReadOnlyStations()
    {
        var admin = await _site.ClientAsync("admin");
        var screen = await Ok(await admin.PostAsJsonAsync("/api/screens", new { name = "总览" + U() }));
        var sid = screen.GetProperty("id").GetInt32();
        object Layout(int col2) => new { name = "中控" + U(), document = new { monitors = new object[] {
            new { name = "左屏", columns = 12, rows = 12, regions = new object[] {
                new { id = "banner", kind = "alarmBanner", row = 1, col = 1, rowSpan = 1, colSpan = 12 },
                new { id = "main", kind = "screen", screenId = sid, row = 2, col = 1, rowSpan = 11, colSpan = 8 },
                new { id = "alarms", kind = "alarmList", row = 2, col = col2, rowSpan = 11, colSpan = 4 } } } } } };
        var notPublished = await admin.PostAsJsonAsync("/api/hmi/layouts", Layout(9));
        Assert.Equal(HttpStatusCode.BadRequest, notPublished.StatusCode);
        await Ok(await admin.PostAsJsonAsync($"/api/screens/{sid}/publish", new { revision = screen.GetProperty("revision").GetInt32() }));
        var overlap = await admin.PostAsJsonAsync("/api/hmi/layouts", Layout(8));
        Assert.Contains("重叠", await overlap.Content.ReadAsStringAsync());
        var layout = await Ok(await admin.PostAsJsonAsync("/api/hmi/layouts", Layout(9)));

        var name = "ws-" + U();
        await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name, layoutId = layout.GetProperty("id").GetInt32(), allowControl = false }));
        var resolved = await Ok(await admin.GetAsync($"/api/hmi/workstations/resolve?name={name}"));
        Assert.Equal(layout.GetProperty("name").GetString(), resolved.GetProperty("layout").GetProperty("name").GetString());

        // 只读工作站：控制请求在进入控制闭环前被拒绝并审计；不带工作站头的同一请求仍按权限处理
        var tag = (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "布局" + U())))).GetProperty("id").GetInt32();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/commands") { Content = JsonContent.Create(new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true }) };
        req.Headers.Add("X-Scada-Workstation", name);
        var denied = await admin.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("只允许监视", await denied.Content.ReadAsStringAsync());
        await Ok(await admin.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true }));

        var detail = await Ok(await admin.GetAsync($"/api/screens/{sid}?draft=true"));
        var del = await admin.DeleteAsync($"/api/screens/{sid}?revision={detail.GetProperty("revision").GetInt32()}");
        Assert.Contains("运行布局", await del.Content.ReadAsStringAsync());

        var package = await Ok(await admin.GetAsync("/api/project/export"));
        var item = package.GetProperty("sections").GetProperty("hmiLayouts").EnumerateArray().Single(x => x.GetProperty("key").GetString() == layout.GetProperty("name").GetString());
        Assert.Contains("screenRef", item.GetRawText());
    }
}
