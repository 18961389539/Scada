using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;
using static Scada.Server.Tests.McpApiTests;

namespace Scada.Server.Tests;

// MCP 组态与生产写工具：画面/运行布局/对象模板采用草稿+发布，停机候选确认触发 OEE 重算，
// 报表模板沿用网页端校验，生成复用归档服务（版本追加、SHA256、审计，绝不返回文件路径）。
public sealed class McpConfigurationTests : IClassFixture<McpConfigurationTests.Site>
{
    public sealed class Site : McpApiTests.Site
    {
        public int PublishedScreenId, LineId, ReasonId;
        public long DowntimeId;

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            var now = ScadaTime.NowMs();
            var factory = Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var screenJson = JsonSerializer.Serialize(new ScreenDocument
            {
                Width = 800, Height = 480,
                Nodes = [new ScreenNode { Id = "title", Type = "text", X = 20, Y = 20, Width = 300, Height = 60, Text = "运行总览" }]
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var screen = new ProcessScreen
            {
                Name = "MCP 已发布画面", DraftJson = screenJson, PublishedJson = screenJson, PublishedName = "MCP 已发布画面",
                Revision = 2, PublishedRevision = 2, UpdatedTs = now, UpdatedBy = "seed"
            };
            var line = new ProductionLine { Name = "MCP 产线", AreaId = AreaA, IdealCycleSec = 60, Enabled = true };
            var reason = new DowntimeReason
            {
                Code = "MCPR", Name = "机械故障", Category = OeeLossCategory.Availability, IsPlanned = false, Enabled = true
            };
            var downtime = new DowntimeEvent
            {
                LineId = line.Id, StartTs = now - 3_600_000, EndTs = now - 1_800_000, State = LineState.Fault,
                Source = "alarm", Confirmed = false, CreatedBy = "seed", CreatedTs = now - 3_600_000
            };
            db.AddRange(screen, line, reason);
            await db.SaveChangesAsync();
            downtime.LineId = line.Id;
            db.DowntimeEvents.Add(downtime);
            await db.SaveChangesAsync();
            PublishedScreenId = screen.Id;
            LineId = line.Id;
            ReasonId = reason.Id;
            DowntimeId = downtime.Id;
        }
    }

    private readonly Site _site;
    public McpConfigurationTests(Site site) => _site = site;
    private IDbContextFactory<ConfigDbContext> Db => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static string Name(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private static async Task<JsonElement> Error(HttpClient client, string name, object? arguments = null)
    {
        var result = await CallAsync(client, name, arguments);
        Assert.True(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), result.ToString());
        return result.GetProperty("structuredContent").GetProperty("error");
    }

    private static object ScreenDocumentBody(string text, int? navigateTo = null) => new
    {
        schemaVersion = 1, width = 800d, height = 480d, background = "#101b2d",
        nodes = new object[]
        {
            new
            {
                id = "title", type = "text", x = 20d, y = 20d, width = 300d, height = 60d, text,
                action = navigateTo is int target ? (object)new { type = "navigate", screenId = target } : new { type = "none" }
            }
        }
    };

    private static object LayoutDocumentBody(int screenId, string title) => new
    {
        monitors = new object[]
        {
            new
            {
                name = "主屏", columns = 12, rows = 12,
                regions = new object[]
                {
                    new { id = "main", kind = "screen", screenId, title, row = 1, col = 1, rowSpan = 8, colSpan = 12 },
                    new { id = "alarms", kind = "alarmList", row = 9, col = 1, rowSpan = 4, colSpan = 12 }
                }
            }
        }
    };

    [Fact]
    public async Task Screen_Tools_Separate_Draft_From_Publish_And_Validate_References()
    {
        using var client = _site.Server.CreateClient();
        var created = McpApiTests.Data(await CallAsync(client, "create_screen", new { name = Name("MCP 画面") }));
        Assert.True(created.GetProperty("success").GetBoolean(), created.ToString());
        var screenId = created.GetProperty("screenId").GetInt32();
        Assert.Equal(1, created.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("publishedRevision").ValueKind);

        var saved = McpApiTests.Data(await CallAsync(client, "save_screen_draft", new
        {
            screenId, revision = 1, name = Name("MCP 画面"), document = ScreenDocumentBody("草稿")
        }));
        Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
        Assert.Equal(2, saved.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("publishedRevision").ValueKind);
        await using (var db = await Db.CreateDbContextAsync())
            Assert.Null((await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == screenId)).PublishedJson);

        var stale = McpApiTests.Data(await CallAsync(client, "save_screen_draft", new
        {
            screenId, revision = 1, name = "过期草稿", document = ScreenDocumentBody("过期")
        }));
        Assert.False(stale.GetProperty("success").GetBoolean());
        Assert.Equal(2, stale.GetProperty("revision").GetInt32());

        var invalid = McpApiTests.Data(await CallAsync(client, "save_screen_draft", new
        {
            screenId, revision = 2, name = "非法草稿",
            document = new { schemaVersion = 1, width = 800d, height = 480d, nodes = new object[] { new { id = "x", type = "bogus", x = 20d, y = 20d, width = 100d, height = 60d } } }
        }));
        Assert.False(invalid.GetProperty("success").GetBoolean());
        Assert.Contains(invalid.GetProperty("issues").EnumerateArray(), x => x.GetString()!.Contains("图元类型不支持"));

        var published = McpApiTests.Data(await CallAsync(client, "publish_screen", new { screenId, revision = 2 }));
        Assert.True(published.GetProperty("success").GetBoolean(), published.ToString());
        Assert.Equal(3, published.GetProperty("revision").GetInt32());
        Assert.Equal(3, published.GetProperty("publishedRevision").GetInt32());
        await using (var db = await Db.CreateDbContextAsync())
        {
            var screen = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == screenId);
            Assert.NotNull(screen.PublishedJson);
            Assert.Equal("mcp:lan", screen.UpdatedBy);
            Assert.True(await db.OperationLogs.AsNoTracking().AnyAsync(
                x => x.TargetType == "ProcessScreen" && x.TargetId == screenId.ToString() && x.Action.StartsWith("发布画面") && x.UserName == "mcp:lan"));
        }

        var blocking = McpApiTests.Data(await CallAsync(client, "create_screen", new { name = Name("MCP 跳转画面") }));
        var blockingId = blocking.GetProperty("screenId").GetInt32();
        McpApiTests.Data(await CallAsync(client, "save_screen_draft", new
        {
            screenId = blockingId, revision = 1, name = "跳转画面",
            document = ScreenDocumentBody("跳转", navigateTo: screenId + 999)
        }));
        var refused = McpApiTests.Data(await CallAsync(client, "publish_screen", new { screenId = blockingId, revision = 2 }));
        Assert.False(refused.GetProperty("success").GetBoolean(), refused.ToString());
        Assert.Contains(refused.GetProperty("issues").EnumerateArray(), x => x.GetString()!.Contains("目标画面"));

        Assert.Equal("not_found", (await Error(client, "publish_screen", new { screenId = 999999, revision = 0 })).GetProperty("code").GetString());
        Assert.Equal("invalid_argument", (await Error(client, "save_screen_draft", new { screenId, revision = 0, name = "", document = ScreenDocumentBody("x") })).GetProperty("code").GetString());
    }

    private static async Task<JsonElement> LibraryItemAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("item");
        }
    }

    [Fact]
    public async Task Screen_Publish_Freezes_Template_Commands_And_User_Svg_Until_Republished()
    {
        using var admin = await _site.ClientAsync("admin");
        using var client = _site.Server.CreateClient();
        var name = Name("MCP 固化画面");
        var templateKey = Name("mcptemplate");
        var symbolKey = Name("mcpsymbol");
        var symbol = "user:" + symbolKey;
        const string oldSvg = "<svg viewBox=\"0 0 100 100\"><rect width=\"80\" height=\"80\" fill=\"#123456\"/></svg>";
        const string newSvg = "<svg viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"#abcdef\"/></svg>";
        object SymbolBody(string svg, int revision = 0) => new { key = symbolKey, name = symbolKey, category = "MCP", kind = "symbol", width = 100, height = 100, svg, revision };
        object TemplateBody(double value, string text, string bind, int revision = 0) => new
        {
            key = templateKey, name = templateKey, category = "MCP", kind = "template", width = 640, height = 300,
            placeholders = new[] { "cmd", "alternate" }, revision,
            document = new ScreenDocument
            {
                Width = 640, Height = 300,
                Nodes = [
                    new() { Id = "symbol", Type = "symbol", Width = 100, Height = 100, Symbol = symbol },
                    new() { Id = "start", Type = "button", X = 120, Width = 100, Height = 40, Text = text, Action = new() { Type = "command", Bind = bind, Value = value } },
                    new() { Id = "panel", Type = "equipment-panel", X = 240, Width = 320, Height = 240,
                        Commands = [new() { Key = "start", Label = text, Bind = bind, Value = value }] }
                ]
            }
        };
        var symbolItem = await LibraryItemAsync(await admin.PostAsJsonAsync("/api/hmi/library", SymbolBody(oldSvg)));
        var templateItem = await LibraryItemAsync(await admin.PostAsJsonAsync("/api/hmi/library", TemplateBody(1, "旧命令", "cmd")));
        var created = McpApiTests.Data(await CallAsync(client, "create_screen", new { name }));
        Assert.True(created.GetProperty("success").GetBoolean(), created.ToString());
        var screenId = created.GetProperty("screenId").GetInt32();
        var document = new ScreenDocument
        {
            Width = 800, Height = 480,
            Nodes = [new() { Id = "instance", Type = "template", TemplateKey = templateKey, Width = 640, Height = 300,
                Bindings = [new() { Key = "cmd", TagId = _site.TagA }, new() { Key = "alternate", TagId = _site.TagB }] }]
        };
        var saved = McpApiTests.Data(await CallAsync(client, "save_screen_draft", new { screenId, revision = 1, name, document }));
        Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
        var published = McpApiTests.Data(await CallAsync(client, "publish_screen", new { screenId, revision = saved.GetProperty("revision").GetInt32() }));
        Assert.True(published.GetProperty("success").GetBoolean(), published.ToString());
        var publishedRevision = published.GetProperty("publishedRevision").GetInt32();
        Assert.Equal(3, publishedRevision);

        void AssertPublication(ProcessScreen row, int tagId, double value, string text, string svg)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var frozen = JsonSerializer.Deserialize<ScreenDocument>(row.PublishedJson!, options)!;
            Assert.Equal(3, frozen.Nodes.Count);
            Assert.All(frozen.Nodes, node =>
            {
                Assert.NotEqual("template", node.Type);
                Assert.Null(node.TemplateKey);
                Assert.Null(node.Bindings);
                Assert.Null(node.Action.Bind);
            });
            Assert.Equal(symbol, frozen.Nodes.Single(x => x.Id == "instance--symbol").Symbol);
            Assert.Equal(svg, Assert.Single(frozen.SymbolAssets!).Value);
            Assert.Equal(symbol, Assert.Single(frozen.SymbolAssets!).Key);
            var button = frozen.Nodes.Single(x => x.Id == "instance--start");
            Assert.Equal("button", button.Type);
            Assert.Equal(text, button.Text);
            Assert.Equal(tagId, button.Action.TagId);
            Assert.Equal(value, button.Action.Value);
            var command = Assert.Single(frozen.Nodes.Single(x => x.Id == "instance--panel").Commands!);
            Assert.Equal(tagId, command.TagId);
            Assert.Equal(value, command.Value);
            Assert.Equal(text, command.Label);
            Assert.Null(command.Bind);
            var draft = JsonSerializer.Deserialize<ScreenDocument>(row.DraftJson, options)!;
            var instance = Assert.Single(draft.Nodes);
            Assert.Equal("template", instance.Type);
            Assert.Equal(templateKey, instance.TemplateKey);
            Assert.Null(draft.SymbolAssets);
        }

        ProcessScreen original;
        await using (var db = await Db.CreateDbContextAsync())
        {
            original = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == screenId);
            AssertPublication(original, _site.TagA, 1, "旧命令", oldSvg);
        }
        await LibraryItemAsync(await admin.PutAsJsonAsync($"/api/hmi/library/{templateItem.GetProperty("id").GetInt32()}",
            TemplateBody(2, "新命令", "alternate", templateItem.GetProperty("revision").GetInt32())));
        await LibraryItemAsync(await admin.PutAsJsonAsync($"/api/hmi/library/{symbolItem.GetProperty("id").GetInt32()}",
            SymbolBody(newSvg, symbolItem.GetProperty("revision").GetInt32())));
        await using (var db = await Db.CreateDbContextAsync())
        {
            var unchanged = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == screenId);
            Assert.Equal(original.PublishedJson, unchanged.PublishedJson);
            Assert.Equal(original.DraftJson, unchanged.DraftJson);
            Assert.Equal(original.Revision, unchanged.Revision);
            Assert.Equal(publishedRevision, unchanged.PublishedRevision);
            AssertPublication(unchanged, _site.TagA, 1, "旧命令", oldSvg);
        }
        var running = await admin.GetFromJsonAsync<JsonElement>($"/api/screens/{screenId}");
        Assert.Equal(publishedRevision, running.GetProperty("publishedRevision").GetInt32());
        Assert.Equal(oldSvg, running.GetProperty("document").GetProperty("symbolAssets").GetProperty(symbol).GetString());

        var republished = McpApiTests.Data(await CallAsync(client, "publish_screen", new { screenId, revision = publishedRevision }));
        Assert.True(republished.GetProperty("success").GetBoolean(), republished.ToString());
        Assert.Equal(publishedRevision + 1, republished.GetProperty("publishedRevision").GetInt32());
        await using (var db = await Db.CreateDbContextAsync())
        {
            var updated = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == screenId);
            Assert.Equal(publishedRevision + 1, updated.PublishedRevision);
            Assert.Equal(original.DraftJson, updated.DraftJson);
            Assert.NotEqual(original.PublishedJson, updated.PublishedJson);
            AssertPublication(updated, _site.TagB, 2, "新命令", newSvg);
        }
    }

    [Fact]
    public async Task Screen_Draft_Tool_Rejects_Symbol_Asset_Injection_Without_Changing_Stored_Documents()
    {
        using var client = _site.Server.CreateClient();
        var name = Name("MCP 快照注入");
        var created = McpApiTests.Data(await CallAsync(client, "create_screen", new { name }));
        var screenId = created.GetProperty("screenId").GetInt32();
        var saved = McpApiTests.Data(await CallAsync(client, "save_screen_draft", new { screenId, revision = 1, name, document = ScreenDocumentBody("原草稿") }));
        Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
        var published = McpApiTests.Data(await CallAsync(client, "publish_screen", new { screenId, revision = 2 }));
        Assert.True(published.GetProperty("success").GetBoolean(), published.ToString());
        ProcessScreen original;
        int originalLogs;
        await using (var db = await Db.CreateDbContextAsync())
        {
            original = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == screenId);
            originalLogs = await db.OperationLogs.CountAsync(x => x.TargetType == "ProcessScreen" && x.TargetId == screenId.ToString());
        }
        foreach (var assets in new Dictionary<string, string>[] { [], new() { ["user:injected"] = "<svg viewBox=\"0 0 100 100\"><rect width=\"80\" height=\"80\"/></svg>" } })
        {
            var result = McpApiTests.Data(await CallAsync(client, "save_screen_draft", new
            {
                screenId, revision = original.Revision, name = "注入后的名称",
                document = new ScreenDocument { SymbolAssets = assets }
            }));
            Assert.False(result.GetProperty("success").GetBoolean(), result.ToString());
            Assert.Contains(result.GetProperty("issues").EnumerateArray(), issue => issue.GetString()!.Contains("符号快照"));
            await using var db = await Db.CreateDbContextAsync();
            var unchanged = await db.ProcessScreens.AsNoTracking().SingleAsync(x => x.Id == screenId);
            Assert.Equal(original.Name, unchanged.Name);
            Assert.Equal(original.DraftJson, unchanged.DraftJson);
            Assert.Equal(original.PublishedJson, unchanged.PublishedJson);
            Assert.Equal(original.Revision, unchanged.Revision);
            Assert.Equal(original.PublishedRevision, unchanged.PublishedRevision);
            Assert.Equal(originalLogs, await db.OperationLogs.CountAsync(x => x.TargetType == "ProcessScreen" && x.TargetId == screenId.ToString()));
        }
    }

    [Fact]
    public async Task Hmi_Layout_Tools_Publish_And_Roll_Back_With_Immutable_Versions()
    {
        using var client = _site.Server.CreateClient();
        var name = Name("MCP 布局");
        var created = McpApiTests.Data(await CallAsync(client, "save_hmi_layout", new
        {
            name, document = LayoutDocumentBody(_site.PublishedScreenId, "主画面")
        }));
        Assert.True(created.GetProperty("success").GetBoolean(), created.ToString());
        var layoutId = created.GetProperty("layoutId").GetInt32();
        Assert.Equal(1, created.GetProperty("revision").GetInt32());
        Assert.True(created.GetProperty("hasDraftChanges").GetBoolean());

        var duplicate = McpApiTests.Data(await CallAsync(client, "save_hmi_layout", new
        {
            name, document = LayoutDocumentBody(_site.PublishedScreenId, "重名")
        }));
        Assert.False(duplicate.GetProperty("success").GetBoolean());
        Assert.Contains("already exists", duplicate.GetProperty("message").GetString());

        var first = McpApiTests.Data(await CallAsync(client, "publish_hmi_layout", new { layoutId, revision = 1, note = "首次发布" }));
        Assert.True(first.GetProperty("success").GetBoolean(), first.ToString());
        Assert.Equal(2, first.GetProperty("revision").GetInt32());
        Assert.Equal(2, first.GetProperty("publishedRevision").GetInt32());
        Assert.Equal("publish", first.GetProperty("kind").GetString());

        var resaved = McpApiTests.Data(await CallAsync(client, "save_hmi_layout", new
        {
            layoutId, revision = 2, name, document = LayoutDocumentBody(_site.PublishedScreenId, "主画面 2")
        }));
        Assert.True(resaved.GetProperty("success").GetBoolean(), resaved.ToString());
        Assert.Equal(3, resaved.GetProperty("revision").GetInt32());
        var second = McpApiTests.Data(await CallAsync(client, "publish_hmi_layout", new { layoutId, revision = 3, note = "第二次发布" }));
        Assert.True(second.GetProperty("success").GetBoolean(), second.ToString());
        Assert.Equal(4, second.GetProperty("revision").GetInt32());

        var stale = McpApiTests.Data(await CallAsync(client, "publish_hmi_layout", new { layoutId, revision = 1, note = "过期发布" }));
        Assert.False(stale.GetProperty("success").GetBoolean());

        var rolledBack = McpApiTests.Data(await CallAsync(client, "publish_hmi_layout", new
        {
            layoutId, revision = 4, note = "回退到首版", rollbackToRevision = 2
        }));
        Assert.True(rolledBack.GetProperty("success").GetBoolean(), rolledBack.ToString());
        Assert.Equal(5, rolledBack.GetProperty("revision").GetInt32());
        Assert.Equal(5, rolledBack.GetProperty("publishedRevision").GetInt32());
        Assert.Equal("rollback", rolledBack.GetProperty("kind").GetString());

        await using var db = await Db.CreateDbContextAsync();
        var versions = await db.HmiLayoutVersions.AsNoTracking().Where(x => x.LayoutId == layoutId).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal([2, 4, 5], versions.Select(x => x.Revision));
        Assert.Equal(["publish", "publish", "rollback"], versions.Select(x => x.Kind));
        Assert.Null(versions[0].RestoredFromRevision);
        Assert.Equal(2, versions[2].RestoredFromRevision);
        Assert.Equal(versions[0].DocumentJson, versions[2].DocumentJson);
        var row = await db.HmiLayouts.AsNoTracking().SingleAsync(x => x.Id == layoutId);
        Assert.Equal(versions[0].DocumentJson, row.PublishedJson);
        Assert.Equal("mcp:lan", row.PublishedBy);
        Assert.True(await db.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "HmiLayout" && x.TargetId == layoutId.ToString() && x.Action.StartsWith("回退运行布局") && x.UserName == "mcp:lan"));

        Assert.Equal("not_found", (await Error(client, "save_hmi_layout", new { layoutId = 999999, revision = 1, name = "x", document = LayoutDocumentBody(_site.PublishedScreenId, "x") })).GetProperty("code").GetString());
        Assert.Equal("not_found", (await Error(client, "publish_hmi_layout", new { layoutId, revision = 5, note = "x", rollbackToRevision = 99 })).GetProperty("code").GetString());
        Assert.Equal("invalid_argument", (await Error(client, "publish_hmi_layout", new { layoutId, revision = 5, note = "" })).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Object_Template_Draft_And_Publish_Append_A_Version()
    {
        var admin = await _site.ClientAsync("admin");
        var pump = await admin.GetFromJsonAsync<JsonElement>("/api/object-templates/examples/pump");
        using var client = _site.Server.CreateClient();
        var name = Name("MCP 对象模板");
        var saved = McpApiTests.Data(await CallAsync(client, "save_object_template", new { name, document = pump }));
        Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
        var templateId = saved.GetProperty("templateId").GetInt32();
        Assert.Equal(1, saved.GetProperty("revision").GetInt32());
        Assert.Equal(0, saved.GetProperty("latestVersion").GetInt32());
        Assert.Empty(saved.GetProperty("errors").EnumerateArray());

        var published = McpApiTests.Data(await CallAsync(client, "publish_object_template", new { templateId, revision = 1 }));
        Assert.True(published.GetProperty("success").GetBoolean(), published.ToString());
        Assert.Equal(2, published.GetProperty("revision").GetInt32());
        Assert.Equal(1, published.GetProperty("version").GetInt32());

        var stale = McpApiTests.Data(await CallAsync(client, "publish_object_template", new { templateId, revision = 1 }));
        Assert.False(stale.GetProperty("success").GetBoolean());

        var invalid = McpApiTests.Data(await CallAsync(client, "save_object_template", new
        {
            templateId, revision = 2, name,
            document = new { schemaVersion = 99, tags = Array.Empty<object>(), alarms = Array.Empty<object>(), panel = new { nodes = Array.Empty<object>() }, bindings = new { }, actionBindings = new { } }
        }));
        Assert.False(invalid.GetProperty("success").GetBoolean());
        Assert.Contains(invalid.GetProperty("errors").EnumerateArray(), x => x.GetString()!.Contains("模板格式版本"));

        await using var db = await Db.CreateDbContextAsync();
        var version = Assert.Single(await db.ObjectTemplateVersions.AsNoTracking().Where(x => x.TemplateId == templateId).ToListAsync());
        Assert.Equal(1, version.Version);
        Assert.Equal("mcp:lan", version.CreatedBy);
        Assert.False(string.IsNullOrWhiteSpace(version.DocumentJson));
        Assert.True(await db.OperationLogs.AsNoTracking().AnyAsync(
            x => x.TargetType == "DeviceObjectTemplate" && x.TargetId == templateId.ToString() && x.Action.StartsWith("发布对象模板") && x.UserName == "mcp:lan"));

        Assert.Equal("not_found", (await Error(client, "publish_object_template", new { templateId = 999999, revision = 1 })).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Downtime_Confirmation_Classifies_And_Requests_An_Oee_Rebuild()
    {
        using var client = _site.Server.CreateClient();
        var confirmed = McpApiTests.Data(await CallAsync(client, "confirm_downtime", new
        {
            downtimeId = _site.DowntimeId, reasonId = _site.ReasonId, note = "MCP 归类为机械故障"
        }));
        Assert.True(confirmed.GetProperty("success").GetBoolean(), confirmed.ToString());
        Assert.True(confirmed.GetProperty("confirmed").GetBoolean());
        Assert.Equal(_site.ReasonId, confirmed.GetProperty("reasonId").GetInt32());
        Assert.Equal("Fault", confirmed.GetProperty("state").GetString());
        var startTs = confirmed.GetProperty("startTs").GetInt64();
        Assert.Equal(startTs, confirmed.GetProperty("rollupFromTs").GetInt64());

        var again = McpApiTests.Data(await CallAsync(client, "confirm_downtime", new
        {
            downtimeId = _site.DowntimeId, reasonId = _site.ReasonId, note = "MCP 归类为机械故障"
        }));
        Assert.True(again.GetProperty("success").GetBoolean());
        Assert.Contains("already confirmed", again.GetProperty("message").GetString());

        await using (var db = await Db.CreateDbContextAsync())
        {
            var row = await db.DowntimeEvents.AsNoTracking().SingleAsync(x => x.Id == _site.DowntimeId);
            Assert.True(row.Confirmed);
            Assert.Equal(_site.ReasonId, row.ReasonId);
            Assert.Equal("MCP 归类为机械故障", row.Note);
            Assert.Equal("mcp:lan", row.UpdatedBy);
            var state = await db.OeeRollupStates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
            Assert.NotNull(state?.RebuildFromTs);
            Assert.True(state!.RebuildFromTs <= startTs);
            Assert.True(await db.OperationLogs.AsNoTracking().AnyAsync(
                x => x.TargetType == "DowntimeEvent" && x.TargetId == _site.DowntimeId.ToString() && x.Action.StartsWith("确认停机") && x.UserName == "mcp:lan"));
        }

        Assert.Equal("not_found", (await Error(client, "confirm_downtime", new { downtimeId = 999999 })).GetProperty("code").GetString());
        Assert.Equal("not_found", (await Error(client, "confirm_downtime", new { downtimeId = _site.DowntimeId, reasonId = 999999 })).GetProperty("code").GetString());
        Assert.Equal("invalid_argument", (await Error(client, "confirm_downtime", new { downtimeId = 0L })).GetProperty("code").GetString());
        Assert.Equal("invalid_argument", (await Error(client, "confirm_downtime", new { downtimeId = _site.DowntimeId, note = new string('x', 501) })).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Report_Template_And_Generation_Reuse_Archive_Versioning()
    {
        using var client = _site.Server.CreateClient();
        var name = Name("MCP 报表");
        object Body(long revision, bool schedule = false, int? templateId = null) => new
        {
            name, templateId, period = "Day", areaId = _site.AreaA, revision, scheduleEnabled = schedule, shiftStarts = "08:00,20:00",
            columns = new object[]
            {
                new { key = "avg", title = "平均温度", kind = "TagAvg", tagId = _site.TagA, decimals = 2, unit = "℃" },
                new { key = "alarms", title = "报警次数", kind = "AlarmCount", decimals = 0 }
            }
        };

        var created = McpApiTests.Data(await CallAsync(client, "save_report_template", Body(0)));
        Assert.True(created.GetProperty("success").GetBoolean(), created.ToString());
        var templateId = created.GetProperty("templateId").GetInt32();
        Assert.Equal(1, created.GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("scheduleSinceTs").ValueKind);

        var invalid = McpApiTests.Data(await CallAsync(client, "save_report_template", new
        {
            name = Name("MCP 非法报表"), period = "Day", areaId = _site.AreaA,
            columns = new object[] { new { key = "avg", title = "平均", kind = "TagAvg" } }
        }));
        Assert.False(invalid.GetProperty("success").GetBoolean());
        Assert.Contains("须选择测点", invalid.GetProperty("message").GetString());

        var missingTag = McpApiTests.Data(await CallAsync(client, "save_report_template", new
        {
            name = Name("MCP 缺测点报表"), period = "Day", areaId = _site.AreaA,
            columns = new object[] { new { key = "avg", title = "平均", kind = "TagAvg", tagId = int.MaxValue } }
        }));
        Assert.False(missingTag.GetProperty("success").GetBoolean());
        Assert.Contains("do not exist", missingTag.GetProperty("message").GetString());

        var scheduled = McpApiTests.Data(await CallAsync(client, "save_report_template", Body(1, schedule: true, templateId: templateId)));
        Assert.True(scheduled.GetProperty("success").GetBoolean(), scheduled.ToString());
        Assert.Equal(2, scheduled.GetProperty("revision").GetInt64());
        var since = scheduled.GetProperty("scheduleSinceTs").GetInt64();
        Assert.True(since > 0);
        var rescheduled = McpApiTests.Data(await CallAsync(client, "save_report_template", Body(2, schedule: true, templateId: templateId)));
        Assert.True(rescheduled.GetProperty("success").GetBoolean(), rescheduled.ToString());
        Assert.Equal(since, rescheduled.GetProperty("scheduleSinceTs").GetInt64());
        var stale = McpApiTests.Data(await CallAsync(client, "save_report_template", Body(1, schedule: true, templateId: templateId)));
        Assert.False(stale.GetProperty("success").GetBoolean());

        var at = ScadaTime.NowMs();
        var first = McpApiTests.Data(await CallAsync(client, "generate_report", new { templateId, at }));
        Assert.True(first.GetProperty("success").GetBoolean(), first.ToString());
        Assert.Equal(1, first.GetProperty("version").GetInt32());
        Assert.Equal(64, first.GetProperty("xlsxSha256").GetString()!.Length);
        Assert.Equal(64, first.GetProperty("pdfSha256").GetString()!.Length);
        Assert.Equal("manual", first.GetProperty("trigger").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("periodKey").GetString()));
        Assert.DoesNotContain("xlsxPath", first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pdfPath", first.ToString(), StringComparison.OrdinalIgnoreCase);

        var second = McpApiTests.Data(await CallAsync(client, "generate_report", new { templateId, at }));
        Assert.True(second.GetProperty("success").GetBoolean(), second.ToString());
        Assert.Equal(2, second.GetProperty("version").GetInt32());
        Assert.Equal(first.GetProperty("periodKey").GetString(), second.GetProperty("periodKey").GetString());

        await using (var db = await Db.CreateDbContextAsync())
        {
            var runs = await db.ReportRuns.AsNoTracking().Where(x => x.TemplateId == templateId).OrderBy(x => x.Version).ToListAsync();
            Assert.Equal([1, 2], runs.Select(x => x.Version));
            Assert.All(runs, run => Assert.Equal("mcp:lan", run.GeneratedBy));
            Assert.All(runs, run => Assert.True(File.Exists(run.XlsxPath) && File.Exists(run.PdfPath)));
            Assert.True(await db.OperationLogs.AsNoTracking().AnyAsync(
                x => x.TargetType == "McpTool" && x.Action == "MCP generate_report" && x.Result == "成功" && x.UserName == "mcp:lan"));
        }

        Assert.Equal("not_found", (await Error(client, "generate_report", new { templateId = 999999 })).GetProperty("code").GetString());
        Assert.Equal("invalid_argument", (await Error(client, "generate_report", new { templateId, at = -1L })).GetProperty("code").GetString());
    }
}
