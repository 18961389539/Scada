using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class ScreenHistoricalVersionTests(AuthorizationTests.Site site) : IClassFixture<AuthorizationTests.Site>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IDbContextFactory<ConfigDbContext> Factory() => site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static string U() => Guid.NewGuid().ToString("N")[..6];
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Document(string background) => JsonSerializer.Serialize(new ScreenDocument { Background = background }, Json);

    [Fact]
    public async Task AsOfAndRecordedRevisionLoadHistoricDocumentNotCurrentPublished()
    {
        var admin = await site.ClientAsync("admin");
        var suffix = U();
        await using var db = await Factory().CreateDbContextAsync();
        var screen = new ProcessScreen
        {
            Name = "历史画面" + suffix, PublishedName = "当前发布" + suffix,
            DraftJson = Document("#00ff00"), PublishedJson = Document("#00ff00"),
            Revision = 2, PublishedRevision = 2, DraftDirty = false, UpdatedBy = "seed"
        };
        db.ProcessScreens.Add(screen);
        await db.SaveChangesAsync();
        db.ProcessScreenVersions.AddRange(
            new ProcessScreenVersion { ScreenId = screen.Id, Revision = 1, Name = "旧版" + suffix, DocumentJson = Document("#111111"), PublishedTs = 1_000, PublishedBy = "old", Kind = "publish" },
            new ProcessScreenVersion { ScreenId = screen.Id, Revision = 2, Name = "当前发布" + suffix, DocumentJson = Document("#00ff00"), PublishedTs = 3_000, PublishedBy = "new", Kind = "publish" });
        var layout = new HmiLayout
        {
            Name = "历史布局" + suffix, DocumentJson = "{}", Revision = 1,
            PublishedName = "历史布局" + suffix, PublishedJson = "{}", PublishedRevision = 1,
            PublishedTs = 2_000, PublishedBy = "layout"
        };
        db.HmiLayouts.Add(layout);
        await db.SaveChangesAsync();
        db.HmiLayoutVersions.Add(new HmiLayoutVersion
        {
            LayoutId = layout.Id, Revision = 1, Name = layout.Name, DocumentJson = "{}",
            PublishedTs = 2_000, PublishedBy = "layout", Note = "初始", Kind = "publish",
            ScreenRevisionsJson = JsonSerializer.Serialize(new Dictionary<string, int> { [screen.Id.ToString()] = 1 })
        });
        await db.SaveChangesAsync();

        var current = await Ok(await admin.GetAsync($"/api/screens/{screen.Id}"));
        Assert.Equal("#00ff00", current.GetProperty("document").GetProperty("background").GetString());

        var asOf = await Ok(await admin.GetAsync($"/api/screens/{screen.Id}?asOf=2000"));
        Assert.Equal("#111111", asOf.GetProperty("document").GetProperty("background").GetString());
        Assert.Equal(1, asOf.GetProperty("revision").GetInt32());
        Assert.True(asOf.GetProperty("historical").GetBoolean());
        Assert.Equal("旧版" + suffix, asOf.GetProperty("name").GetString());

        var recorded = await Ok(await admin.GetAsync($"/api/screens/{screen.Id}?revision=1"));
        Assert.Equal("#111111", recorded.GetProperty("document").GetProperty("background").GetString());

        var version = await Ok(await admin.GetAsync($"/api/hmi/layouts/{layout.Id}/versions/1"));
        Assert.Equal(1, version.GetProperty("screenRevisions").GetProperty(screen.Id.ToString()).GetInt32());

        using var missing = await admin.GetAsync($"/api/screens/{screen.Id}?asOf=500");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
