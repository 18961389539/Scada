using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class KioskPlaylistTests(AuthorizationTests.Site site) : IClassFixture<AuthorizationTests.Site>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IDbContextFactory<ConfigDbContext> Factory() => site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static string U() => Guid.NewGuid().ToString("N")[..6];
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> SeedPublishedScreenAsync(string name)
    {
        await using var db = await Factory().CreateDbContextAsync();
        var document = JsonSerializer.Serialize(new ScreenDocument(), Json);
        var screen = new ProcessScreen
        {
            Name = name, PublishedName = name, DraftJson = document, PublishedJson = document,
            Revision = 1, PublishedRevision = 1, DraftDirty = false, UpdatedBy = "seed"
        };
        db.ProcessScreens.Add(screen);
        await db.SaveChangesAsync();
        db.ProcessScreenVersions.Add(new ProcessScreenVersion
        {
            ScreenId = screen.Id, Revision = 1, Name = name, DocumentJson = document,
            PublishedTs = 1000, PublishedBy = "seed", Kind = "publish"
        });
        await db.SaveChangesAsync();
        return screen.Id;
    }

    [Fact]
    public async Task CanSaveStartAndExportPlaylistWithTwoScreens()
    {
        var admin = await site.ClientAsync("admin");
        var suffix = U();
        var first = await SeedPublishedScreenAsync("列表画面A" + suffix);
        var second = await SeedPublishedScreenAsync("列表画面B" + suffix);
        var created = await Ok(await admin.PostAsJsonAsync("/api/kiosk-playlists", new
        {
            name = "走廊轮播" + suffix,
            screenIds = new[] { first, second },
            intervalSeconds = 15,
            monitorOnly = true
        }));
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal(new[] { first, second }, created.GetProperty("screenIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.Equal(15, created.GetProperty("intervalSeconds").GetInt32());
        Assert.True(created.GetProperty("monitorOnly").GetBoolean());

        var loaded = await Ok(await admin.GetAsync($"/api/kiosk-playlists/{id}"));
        Assert.Equal("走廊轮播" + suffix, loaded.GetProperty("name").GetString());
        Assert.Equal(2, loaded.GetProperty("screenIds").GetArrayLength());

        var package = JsonNode.Parse(await (await admin.GetAsync("/api/project/export")).Content.ReadAsStringAsync())!.AsObject();
        var items = package["sections"]!["kioskPlaylists"]!.AsArray().OfType<JsonObject>()
            .Select(x => x["data"]!.AsObject()).Single(x => x["name"]!.GetValue<string>() == "走廊轮播" + suffix);
        Assert.Equal(new[] { "列表画面A" + suffix, "列表画面B" + suffix }, items["screenRefs"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.True(items["monitorOnly"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OperatorCanReadButNotWriteAndUnpublishedScreenIsRejected()
    {
        var admin = await site.ClientAsync("admin");
        var op = await site.ClientAsync("opA");
        var suffix = U();
        var published = await SeedPublishedScreenAsync("可见画面" + suffix);
        await using (var db = await Factory().CreateDbContextAsync())
        {
            db.ProcessScreens.Add(new ProcessScreen { Name = "草稿画面" + suffix, DraftJson = "{}", UpdatedBy = "seed" });
            await db.SaveChangesAsync();
        }
        await using var db2 = await Factory().CreateDbContextAsync();
        var draftId = await db2.ProcessScreens.Where(x => x.Name == "草稿画面" + suffix).Select(x => x.Id).SingleAsync();

        using var rejected = await admin.PostAsJsonAsync("/api/kiosk-playlists", new { name = "非法" + suffix, screenIds = new[] { published, draftId } });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var created = await Ok(await admin.PostAsJsonAsync("/api/kiosk-playlists", new { name = "只读列表" + suffix, screenIds = new[] { published }, intervalSeconds = 20 }));
        var list = await Ok(await op.GetAsync("/api/kiosk-playlists"));
        Assert.Contains(list.EnumerateArray(), item => item.GetProperty("id").GetInt32() == created.GetProperty("id").GetInt32());
        using var forbidden = await op.PostAsJsonAsync("/api/kiosk-playlists", new { name = "操作员新建" + suffix, screenIds = new[] { published } });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }
    [Fact]
    public async Task EnteringMonitorPlaylistBlocksControlUntilLeave()
    {
        var admin = await site.ClientAsync("admin");
        var suffix = U();
        var screen = await SeedPublishedScreenAsync("监视画面" + suffix);
        var created = await Ok(await admin.PostAsJsonAsync("/api/kiosk-playlists", new
        {
            name = "监视列表" + suffix,
            screenIds = new[] { screen },
            intervalSeconds = 20,
            monitorOnly = true
        }));
        var id = created.GetProperty("id").GetInt32();

        try
        {
            using (var enter = await admin.PostAsJsonAsync($"/api/kiosk-playlists/{id}/enter-monitor", new { }))
                Assert.True(enter.IsSuccessStatusCode, await enter.Content.ReadAsStringAsync());

            using (var blocked = await admin.PostAsJsonAsync("/api/annunciators/silence-visible", new { }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
                var body = await blocked.Content.ReadAsStringAsync();
                Assert.Contains("监视专用", body);
            }
        }
        finally
        {
            using var leave = await admin.PostAsync("/api/kiosk-playlists/leave-monitor", null);
            Assert.True(leave.IsSuccessStatusCode, await leave.Content.ReadAsStringAsync());
        }

        using var after = await admin.PostAsJsonAsync("/api/annunciators/silence-visible", new { });
        Assert.NotEqual(HttpStatusCode.Forbidden, after.StatusCode);
    }

    [Fact]
    public async Task ASecondLoginDoesNotUnlockAnotherTokensMonitorSession()
    {
        var wall = await site.ClientAsync("admin");
        var suffix = U();
        var screen = await SeedPublishedScreenAsync("监视画面" + suffix);
        var created = await Ok(await wall.PostAsJsonAsync("/api/kiosk-playlists", new
        {
            name = "监视列表" + suffix,
            screenIds = new[] { screen },
            intervalSeconds = 20,
            monitorOnly = true
        }));
        var id = created.GetProperty("id").GetInt32();

        try
        {
            using (var enter = await wall.PostAsJsonAsync($"/api/kiosk-playlists/{id}/enter-monitor", new { }))
                Assert.True(enter.IsSuccessStatusCode, await enter.Content.ReadAsStringAsync());

            using var anonymous = site.CreateClient();
            using var login = await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "admin123" });
            Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
            var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            using var engineer = site.CreateClient();
            engineer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using (var stillBlocked = await wall.PostAsJsonAsync("/api/annunciators/silence-visible", new { }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, stillBlocked.StatusCode);
                Assert.Contains("监视专用", await stillBlocked.Content.ReadAsStringAsync());
            }

            using var allowed = await engineer.PostAsJsonAsync("/api/annunciators/silence-visible", new { });
            Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
        }
        finally
        {
            using var leave = await wall.PostAsync("/api/kiosk-playlists/leave-monitor", null);
            Assert.True(leave.IsSuccessStatusCode, await leave.Content.ReadAsStringAsync());
        }
    }
}
