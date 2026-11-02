using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class ScreenPublicationTests(ControlTests.ControlSite site) : IClassFixture<ControlTests.ControlSite>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Key() => "frozen" + Guid.NewGuid().ToString("N")[..8];
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task<(int Id, int Revision)> Save(HttpClient client, ScreenDocument document)
    {
        var name = Key();
        var created = await Ok(await client.PostAsJsonAsync("/api/screens", new { name }));
        var id = created.GetProperty("id").GetInt32();
        var saved = await Ok(await client.PutAsJsonAsync($"/api/screens/{id}", new { name, revision = 1, document }));
        return (id, saved.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task SymbolAssetsAreFrozenAndRemainUsableAfterTheLibraryItemIsRemoved()
    {
        var client = await site.ClientAsync("admin");
        var key = Key();
        const string oldSvg = "<svg viewBox=\"0 0 100 100\"><rect width=\"80\" height=\"80\"/></svg>";
        const string newSvg = "<svg viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"40\"/></svg>";
        object Symbol(string svg, int revision = 0) => new { key, name = key, kind = "symbol", width = 100, height = 100, svg, revision };
        var item = (await Ok(await client.PostAsJsonAsync("/api/hmi/library", Symbol(oldSvg)))).GetProperty("item");
        var (id, revision) = await Save(client, new() { Nodes = [new() { Id = "symbol", Type = "symbol", Symbol = $"user:{key}" }] });
        var first = await Ok(await client.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision }));
        var pubRevision = first.GetProperty("publishedRevision").GetInt32();
        var itemId = item.GetProperty("id").GetInt32();
        await Ok(await client.PutAsJsonAsync($"/api/hmi/library/{itemId}", Symbol(newSvg, item.GetProperty("revision").GetInt32())));
        var frozen = await Ok(await client.GetAsync($"/api/screens/{id}"));
        Assert.Equal(pubRevision, frozen.GetProperty("publishedRevision").GetInt32());
        Assert.Equal(oldSvg, frozen.GetProperty("document").GetProperty("symbolAssets").GetProperty($"user:{key}").GetString());
        var draft = await Ok(await client.GetAsync($"/api/screens/{id}?draft=true"));
        Assert.False(draft.GetProperty("document").TryGetProperty("symbolAssets", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/hmi/library/{itemId}")).StatusCode);
        var saved = await Ok(await client.PutAsJsonAsync($"/api/screens/{id}", new { name = key, revision = pubRevision, document = new ScreenDocument() }));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/hmi/library/{itemId}")).StatusCode);
        var afterRemoval = await Ok(await client.GetAsync($"/api/screens/{id}"));
        Assert.Equal(frozen.GetProperty("document").GetRawText(), afterRemoval.GetProperty("document").GetRawText());
        Assert.True(saved.GetProperty("revision").GetInt32() > pubRevision);
    }

    [Theory]
    [InlineData("template")]
    [InlineData("symbol")]
    public async Task LegacyLibraryPublicationsRequireExplicitRepublishingForDisplayAndControl(string kind)
    {
        var client = await site.ClientAsync("admin");
        var key = Key();
        var tag = (await Ok(await client.PostAsJsonAsync("/api/tags", site.TagBody(site.DeviceA, key)))).GetProperty("id").GetInt32();
        var libraryDocument = new ScreenDocument { Nodes = [new() { Id = "text", Type = "text", Width = 100, Height = 40 }] };
        await Ok(await client.PostAsJsonAsync("/api/hmi/library", new { key, name = key, kind, width = 200, height = 200, svg = "<svg></svg>", document = libraryDocument, placeholders = Array.Empty<string>() }));
        var document = new ScreenDocument { Nodes = [
            new() { Id = "dependency", Type = kind, Width = 200, Height = 200, TemplateKey = kind == "template" ? key : null, Symbol = kind == "symbol" ? $"user:{key}" : null },
            new() { Id = "command", Type = "button", Y = 220, Action = new() { Type = "command", TagId = tag, Value = 1 } }
        ] };
        var json = JsonSerializer.Serialize(document, Json);
        var factory = site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        int id;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = db.ProcessScreens.Add(new ProcessScreen { Name = key, PublishedName = key, DraftJson = json, PublishedJson = json, Revision = 2, PublishedRevision = 2, UpdatedBy = "legacy" }).Entity;
            await db.SaveChangesAsync(); id = row.Id;
        }
        var unavailable = await Ok(await client.GetAsync($"/api/screens/{id}"));
        Assert.True(unavailable.GetProperty("requiresRepublish").GetBoolean());
        Assert.Contains("重新发布", unavailable.GetProperty("message").GetString());
        Assert.Equal(2, unavailable.GetProperty("document").GetProperty("nodes").GetArrayLength());
        await Ok(await client.GetAsync($"/api/screens/{id}?draft=true"));
        var collector = site.Services.GetRequiredService<ControlTests.FakeCollector>();
        var before = collector.Count(tag);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/screens/{id}/actions/command", new { publishedRevision = 2, confirmed = true, requestId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(before, collector.Count(tag));
        var published = await Ok(await client.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision = 2 }));
        var publishedRevision = published.GetProperty("publishedRevision").GetInt32();
        await Ok(await client.GetAsync($"/api/screens/{id}"));
        var result = await Ok(await client.PostAsJsonAsync($"/api/screens/{id}/actions/command", new { publishedRevision, confirmed = true, requestId = Guid.NewGuid() }));
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(before + 1, collector.Count(tag));
    }

    [Fact]
    public async Task DraftsCannotInjectPublishedSymbolAssets()
    {
        var client = await site.ClientAsync("admin");
        var created = await Ok(await client.PostAsJsonAsync("/api/screens", new { name = Key() }));
        var id = created.GetProperty("id").GetInt32();
        var document = new ScreenDocument { SymbolAssets = new() { ["user:injected"] = "<svg><script/></svg>" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/screens/{id}", new { name = Key(), revision = 1, document })).StatusCode);
        var issues = (await Ok(await client.PostAsJsonAsync($"/api/screens/validate?screenId={id}", document))).GetProperty("issues");
        Assert.Contains(issues.EnumerateArray(), issue => issue.GetProperty("message").GetString()!.Contains("符号快照"));
        var draft = await Ok(await client.GetAsync($"/api/screens/{id}?draft=true"));
        Assert.Equal(1, draft.GetProperty("revision").GetInt32());
        Assert.False(draft.GetProperty("document").TryGetProperty("symbolAssets", out _));
        document.SymbolAssets = null;
        document.Nodes = [new() { Id = "instance", Type = "template", TemplateKey = "test", Bindings = [new() { Key = "same" }, new() { Key = "same" }] }];
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/screens/{id}", new { name = Key(), revision = 1, document })).StatusCode);
    }

    [Fact]
    public async Task TemplateFramePipeConnectionsKeepTheirPublishedGeometry()
    {
        var client = await site.ClientAsync("admin");
        var key = Key();
        await Ok(await client.PostAsJsonAsync("/api/hmi/library", new { key, name = key, kind = "template", width = 200, height = 200, placeholders = Array.Empty<string>(),
            document = new ScreenDocument { Nodes = [new() { Id = "inner", Type = "text", Width = 100, Height = 40 }] } }));
        var document = new ScreenDocument { Nodes = [
            new() { Id = "frame", Type = "template", TemplateKey = key, X = 100, Y = 100, Width = 200, Height = 200 },
            new() { Id = "motor", Type = "motor", X = 500, Y = 100, Width = 100, Height = 100 },
            new() { Id = "pipe", Type = "polyline", X = 200, Y = 100, Width = 200, Height = 100,
                Source = new("frame", "right"), Target = new("motor", "left") }
        ] };
        var (id, revision) = await Save(client, document);
        await Ok(await client.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision }));
        var publication = (await Ok(await client.GetAsync($"/api/screens/{id}"))).GetProperty("document").Deserialize<ScreenDocument>(Json)!;
        var pipe = publication.Nodes.Single(node => node.Id == "pipe");
        Assert.Equal(300, pipe.X); Assert.Equal(150, pipe.Y);
        Assert.Equal(200, pipe.Width); Assert.Equal(50, pipe.Height);
        Assert.Equal([new ScreenPoint(0, 100), new(50, 100), new(50, 0), new(100, 0)], pipe.Points!);
        Assert.Null(pipe.Source); Assert.Null(pipe.Target);
        Assert.Contains(publication.Nodes, node => node.Id == "frame--inner");
        var draft = (await Ok(await client.GetAsync($"/api/screens/{id}?draft=true"))).GetProperty("document").Deserialize<ScreenDocument>(Json)!;
        Assert.Equal("frame", draft.Nodes.Single(node => node.Id == "pipe").Source!.NodeId);
        Assert.Equal("frame", document.Nodes.Single(node => node.Id == "pipe").Source!.NodeId);
    }

    [Fact]
    public async Task PublicationRejectsTheExpandedNodeLimitAndDraftsCannotClaimTheExpansionSeparator()
    {
        var client = await site.ClientAsync("admin");
        var key = Key();
        var template = new ScreenDocument { Nodes = Enumerable.Range(0, 200).Select(index => new ScreenNode { Id = $"n{index}", Type = "text", Width = 16, Height = 16 }).ToList() };
        await Ok(await client.PostAsJsonAsync("/api/hmi/library", new { key, name = key, kind = "template", width = 200, height = 200, placeholders = Array.Empty<string>(), document = template }));
        var document = new ScreenDocument { Nodes = Enumerable.Range(0, 6).Select(index => new ScreenNode { Id = $"instance{index}", Type = "template", TemplateKey = key, Width = 200, Height = 200 }).ToList() };
        var (id, revision) = await Save(client, document);
        var tooMany = await client.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("1000", await tooMany.Content.ReadAsStringAsync());
        document.Nodes = [document.Nodes[0], new() { Id = "instance0--n0", Type = "text" }];
        var reserved = await client.PutAsJsonAsync($"/api/screens/{id}", new { name = key, revision, document });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        Assert.Contains("图元编号无效", await reserved.Content.ReadAsStringAsync());
        var unchanged = await Ok(await client.GetAsync($"/api/screens/{id}?draft=true"));
        Assert.Equal(JsonValueKind.Null, unchanged.GetProperty("publishedRevision").ValueKind);
        Assert.Equal(6, unchanged.GetProperty("document").GetProperty("nodes").GetArrayLength());
    }

    [Fact]
    public async Task UsageNamesObjectInstancesAndPublishedJumpsThatBlockDelete()
    {
        var client = await site.ClientAsync("admin");
        var (targetId, targetRevision) = await Save(client, new ScreenDocument { Nodes = [new ScreenNode { Id = "t", Type = "text", Text = "target" }] });
        await Ok(await client.PostAsJsonAsync($"/api/screens/{targetId}/publish", new { revision = targetRevision }));
        var (jumpId, jumpRevision) = await Save(client, new ScreenDocument { Nodes = [new ScreenNode { Id = "jump", Type = "text", Text = "go", Action = new ScreenAction { Type = "navigate", ScreenId = targetId } }] });
        await Ok(await client.PostAsJsonAsync($"/api/screens/{jumpId}/publish", new { revision = jumpRevision }));
        var (popupId, popupRevision) = await Save(client, new ScreenDocument { Nodes = [new ScreenNode { Id = "pop", Type = "text", Text = "pop", Action = new ScreenAction { Type = "popup", ScreenId = targetId } }] });
        await Ok(await client.PostAsJsonAsync($"/api/screens/{popupId}/publish", new { revision = popupRevision }));
        var (plainId, plainRevision) = await Save(client, new ScreenDocument { Nodes = [new ScreenNode { Id = "plain", Type = "text", Text = "plain" }] });
        await Ok(await client.PostAsJsonAsync($"/api/screens/{plainId}/publish", new { revision = plainRevision }));
        await Save(client, new ScreenDocument { Nodes = [new ScreenNode { Id = "draft", Type = "text", Text = "draft", Action = new ScreenAction { Type = "navigate", ScreenId = targetId } }] });
        var (brokenId, brokenRevision) = await Save(client, new ScreenDocument { Nodes = [new ScreenNode { Id = "broken", Type = "text", Text = "broken" }] });
        await Ok(await client.PostAsJsonAsync($"/api/screens/{brokenId}/publish", new { revision = brokenRevision }));

        await using var db = await site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();
        var device = new Device { Name = Key(), Enabled = false };
        var template = new DeviceObjectTemplate { Name = Key(), UpdatedBy = "test" };
        db.Devices.Add(device);
        db.ObjectTemplates.Add(template);
        await db.SaveChangesAsync();
        db.ObjectInstances.Add(new DeviceObjectInstance { TemplateId = template.Id, DeviceId = device.Id, Name = "泵对象", ScreenId = targetId, UpdatedBy = "test" });
        var broken = await db.ProcessScreens.SingleAsync(x => x.Id == brokenId);
        var original = broken.PublishedJson;
        broken.PublishedJson = "{";
        await db.SaveChangesAsync();
        try
        {
            var usage = await Ok(await client.GetAsync($"/api/screens/{targetId}/usage"));
            var names = usage.GetProperty("referencedBy").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
            Assert.Contains((await Ok(await client.GetAsync($"/api/screens/{jumpId}"))).GetProperty("name").GetString(), names);
            Assert.Contains((await Ok(await client.GetAsync($"/api/screens/{popupId}"))).GetProperty("name").GetString(), names);
            Assert.DoesNotContain((await Ok(await client.GetAsync($"/api/screens/{plainId}"))).GetProperty("name").GetString(), names);
            Assert.Equal("泵对象", usage.GetProperty("objectInstances").EnumerateArray().Single().GetProperty("name").GetString());
            Assert.True(usage.GetProperty("unreadable").GetBoolean());
            Assert.Equal(JsonValueKind.Array, usage.GetProperty("layouts").ValueKind);
        }
        finally
        {
            broken.PublishedJson = original;
            await db.SaveChangesAsync();
        }
    }
}
