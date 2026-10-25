using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Scada.Server.Tests;

// 图库：用户符号 SVG 安全校验、参数化模板（占位符绑定、一处修改全部实例生效）、模板内控制走控制闭环、引用保护、工程包。
public sealed class HmiLibraryTests : IClassFixture<ControlTests.ControlSite>
{
    private readonly ControlTests.ControlSite _site;
    public HmiLibraryTests(ControlTests.ControlSite site) => _site = site;
    private ControlTests.FakeCollector Collector => _site.Services.GetRequiredService<ControlTests.FakeCollector>();
    private static string U() => Guid.NewGuid().ToString("N")[..5];
    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode is HttpStatusCode.NoContent ? default : await r.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static object Node(string id, string type, double x, double y, double w, double h, string? bind = null, object? action = null, string? symbol = null) => new
    {
        id, type, x, y, width = w, height = h, rotation = 0, text = id, color = "#8c96a0", activeColor = "#e9ecef", unit = "", decimals = 1, min = 0, max = 100, activeThreshold = 0.5,
        tagId = (int?)null, bind, symbol, action = action ?? new { type = "none", screenId = (int?)null, tagId = (int?)null, value = 0 }
    };
    private static object Template(string key, double value, string[] placeholders) => new
    {
        key, name = "泵面板 " + key, category = "泵", kind = "template", width = 200, height = 120, placeholders,
        document = new { schemaVersion = 1, width = 200, height = 120, background = "#101b2d", nodes = new[] {
            Node("sym", "symbol", 0, 0, 100, 100, bind: "run", symbol: "isa:pump"),
            Node("start", "button", 100, 0, 100, 40, action: new { type = "command", screenId = (int?)null, tagId = (int?)null, value, bind = "cmd" }) } }
    };

    [Fact]
    public async Task TemplateInstancesResolveBindingsAndFollowTemplateChanges()
    {
        var admin = await _site.ClientAsync("admin");
        var run = (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "运行" + U())))).GetProperty("id").GetInt32();
        var cmd = (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "启停" + U())))).GetProperty("id").GetInt32();
        var key = "pump" + U();
        // 未声明的占位符被拒绝
        var bad = await admin.PostAsJsonAsync("/api/hmi/library", Template(key, 1, ["run"]));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); Assert.Contains("cmd", await bad.Content.ReadAsStringAsync());
        var item = (await Ok(await admin.PostAsJsonAsync("/api/hmi/library", Template(key, 1, ["run", "cmd"])))).GetProperty("item");

        var screen = (await Ok(await admin.PostAsJsonAsync("/api/screens", new { name = "模板画面" + U() }))).GetProperty("id").GetInt32();
        object Doc(bool bindCmd) => new { schemaVersion = 1, width = 1200, height = 720, background = "#101b2d", nodes = new object[] { new {
            id = "p1", type = "template", x = 10, y = 10, width = 400, height = 240, rotation = 0, text = "P1", color = "#8c96a0", activeColor = "#e9ecef", unit = "", decimals = 1, min = 0, max = 100, activeThreshold = 0.5,
            tagId = (int?)null, templateKey = key, bindings = bindCmd ? new object[] { new { key = "run", tagId = run }, new { key = "cmd", tagId = cmd } } : [new { key = "run", tagId = run }],
            action = new { type = "none", screenId = (int?)null, tagId = (int?)null, value = 0 } } } };
        var rev = (await Ok(await admin.PutAsJsonAsync($"/api/screens/{screen}", new { name = "模板画面", revision = 1, document = Doc(false) }))).GetProperty("revision").GetInt32();
        var issues = (await Ok(await admin.PostAsJsonAsync($"/api/screens/validate?screenId={screen}", Doc(false)))).GetProperty("issues");
        Assert.Contains(issues.EnumerateArray(), x => x.GetProperty("nodeId").GetString() == "p1" && x.GetProperty("message").GetString()!.Contains("cmd"));
        rev = (await Ok(await admin.PutAsJsonAsync($"/api/screens/{screen}", new { name = "模板画面", revision = rev, document = Doc(true) }))).GetProperty("revision").GetInt32();
        var published = await Ok(await admin.PostAsJsonAsync($"/api/screens/{screen}/publish", new { revision = rev }));
        var pubRev = published.GetProperty("publishedRevision").GetInt32();

        // 模板内控制：服务端按实例绑定解析输出测点，走控制闭环
        var before = Collector.Count(cmd);
        var result = await Ok(await admin.PostAsJsonAsync($"/api/screens/{screen}/actions/p1--start", new { publishedRevision = pubRev, requestId = Guid.NewGuid(), confirmed = true }));
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(before + 1, Collector.Count(cmd));
        Assert.Equal(1, Collector.Writes.Last(x => x.TagId == cmd).Value);

        // 修改模板一次，已发布画面的实例随之生效
        var upd = Template(key, 2, ["run", "cmd"]);
        var saved = await Ok(await admin.PutAsJsonAsync($"/api/hmi/library/{item.GetProperty("id").GetInt32()}", new
        {
            key, name = "泵面板", category = "泵", kind = "template", width = 200, height = 120, placeholders = new[] { "run", "cmd" },
            document = ((dynamic)upd).document, revision = item.GetProperty("revision").GetInt32()
        }));
        Assert.Contains(saved.GetProperty("usedBy").EnumerateArray(), x => x.GetString()!.StartsWith("模板画面"));
        await Ok(await admin.PostAsJsonAsync($"/api/screens/{screen}/actions/p1--start", new { publishedRevision = pubRev, requestId = Guid.NewGuid(), confirmed = true }));
        Assert.Equal(2, Collector.Writes.Last(x => x.TagId == cmd).Value);

        // 被引用时不能删除
        var del = await admin.DeleteAsync($"/api/hmi/library/{item.GetProperty("id").GetInt32()}");
        Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);

        // 工程包包含图库分区
        var package = await Ok(await admin.GetAsync("/api/project/export"));
        Assert.Contains(package.GetProperty("sections").GetProperty("hmiLibrary").EnumerateArray(), x => x.GetProperty("key").GetString() == key);
    }

    [Fact]
    public async Task UserSymbolsRejectActiveContent()
    {
        var admin = await _site.ClientAsync("admin");
        object Sym(string svg) => new { key = "s" + U(), name = "符号", category = "自定义", kind = "symbol", svg, width = 100, height = 100 };
        foreach (var svg in new[] { "<svg><script>alert(1)</script></svg>", "<svg><rect onclick=\"x()\"/></svg>", "<svg><image href=\"http://x/y.png\"/></svg>", "<svg><foreignObject/></svg>", "<div/>" })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/hmi/library", Sym(svg))).StatusCode);
        await Ok(await admin.PostAsJsonAsync("/api/hmi/library", Sym("<svg viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"currentColor\"/></svg>")));
        var viewer = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/hmi/library", Sym("<svg/>"))).StatusCode);
        Assert.True((await viewer.GetAsync("/api/hmi/library")).IsSuccessStatusCode);
    }
}
