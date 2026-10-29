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
        var notPublished = await Ok(await admin.PostAsJsonAsync("/api/hmi/layouts", Layout(9)));
        var invalidPublish = await admin.PostAsJsonAsync($"/api/hmi/layouts/{notPublished.GetProperty("id").GetInt32()}/publish", new { revision = notPublished.GetProperty("revision").GetInt32(), note = "未发布画面不能进入运行布局" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidPublish.StatusCode);
        await Ok(await admin.PostAsJsonAsync($"/api/screens/{sid}/publish", new { revision = screen.GetProperty("revision").GetInt32() }));
        var overlap = await Ok(await admin.PostAsJsonAsync("/api/hmi/layouts", Layout(8)));
        var overlapPublish = await admin.PostAsJsonAsync($"/api/hmi/layouts/{overlap.GetProperty("id").GetInt32()}/publish", new { revision = overlap.GetProperty("revision").GetInt32(), note = "重叠校验" });
        Assert.Equal(HttpStatusCode.BadRequest, overlapPublish.StatusCode);
        Assert.Contains("重叠", await overlapPublish.Content.ReadAsStringAsync());
        var layout = await Ok(await admin.PostAsJsonAsync("/api/hmi/layouts", Layout(9)));
        await Ok(await admin.PostAsJsonAsync($"/api/hmi/layouts/{layout.GetProperty("id").GetInt32()}/publish", new { revision = layout.GetProperty("revision").GetInt32(), note = "初次发布" }));

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

    [Fact]
    public async Task StableWorkstationBindingSurvivesRenameAndRejectsDeletedOrInvalidIds()
    {
        var admin = await _site.ClientAsync("admin");
        var oldName = "ws-" + U();
        var station = await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name = oldName, allowControl = false }));
        var id = station.GetProperty("id").GetInt32();
        var newName = "renamed-" + U();
        station = await Ok(await admin.PutAsJsonAsync($"/api/hmi/workstations/{id}", new { name = newName, allowControl = false, expectedUpdatedTs = station.GetProperty("updatedTs").GetInt64() }));
        var resolved = await Ok(await admin.GetAsync($"/api/hmi/workstations/resolve?id={id}&name={oldName}"));
        Assert.Equal(newName, resolved.GetProperty("workstation").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/hmi/workstations/resolve?name={oldName}")).StatusCode);
        Assert.Equal(id, (await Ok(await admin.GetAsync($"/api/hmi/workstations/resolve?name={newName}"))).GetProperty("workstation").GetProperty("id").GetInt32());
        var tag = (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "工位" + U())))).GetProperty("id").GetInt32();
        HttpRequestMessage Command(string? stableId, string name)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/commands") { Content = JsonContent.Create(new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true }) };
            if (stableId is not null) request.Headers.Add("X-Scada-Workstation-Id", stableId);
            request.Headers.Add("X-Scada-Workstation", name);
            return request;
        }
        var readOnly = await admin.SendAsync(Command(id.ToString(), oldName));
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);
        Assert.Contains("只允许监视", await readOnly.Content.ReadAsStringAsync());
        // Legacy name clients still resolve and receive the same read-only policy.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.SendAsync(Command(null, newName))).StatusCode);
        station = await Ok(await admin.PutAsJsonAsync($"/api/hmi/workstations/{id}", new { name = newName, allowControl = true, expectedUpdatedTs = station.GetProperty("updatedTs").GetInt64() }));
        await Ok(await admin.SendAsync(Command(id.ToString(), oldName)));
        await Ok(await admin.DeleteAsync($"/api/hmi/workstations/{id}?updatedTs={station.GetProperty("updatedTs").GetInt64()}"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/hmi/workstations/resolve?id={id}")).StatusCode);
        // A stale ID must never fall back to a different workstation with the old name.
        await Ok(await admin.PostAsJsonAsync("/api/hmi/workstations", new { name = oldName, allowControl = true }));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.SendAsync(Command(id.ToString(), oldName))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.SendAsync(Command("invalid", oldName))).StatusCode);
    }

    [Fact]
    public async Task MobileControlPolicyFollowsConfigAndRoles()
    {
        var admin = await _site.ClientAsync("admin");
        var tag = (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "手机" + U())))).GetProperty("id").GetInt32();
        HttpRequestMessage Mobile() { var r = new HttpRequestMessage(HttpMethod.Post, "/api/commands") { Content = JsonContent.Create(new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true }) }; r.Headers.Add("X-Scada-Client", "mobile"); return r; }
        var options = _site.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Scada.Server.Auth.MobileOptions>>().CurrentValue;
        try
        {
            Assert.True((await Ok(await admin.GetAsync("/api/client-policy"))).GetProperty("mobileControl").GetBoolean());
            await Ok(await admin.SendAsync(Mobile()));
            options.ControlRoles = ["Operator"];
            Assert.False((await Ok(await admin.GetAsync("/api/client-policy"))).GetProperty("mobileControl").GetBoolean());
            var denied = await admin.SendAsync(Mobile());
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Contains("移动端", await denied.Content.ReadAsStringAsync());
            options.ControlRoles = ["Admin"];
            await Ok(await admin.SendAsync(Mobile()));
            options.AllowControl = false;
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.SendAsync(Mobile())).StatusCode);
            // 桌面端不受移动端开关影响
            await Ok(await admin.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true }));
        }
        finally { options.AllowControl = true; options.ControlRoles = []; }
    }
}
