using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class AnnunciatorRevisionApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public AnnunciatorRevisionApiTests(AuthorizationTests.Site site) => _site = site;
    private IDbContextFactory<ConfigDbContext> Factory() => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<int> WritableTagAsync()
    {
        await using var db = await Factory().CreateDbContextAsync();
        var device = await db.Devices.AsNoTracking().FirstAsync(x => x.AreaId == _site.AreaA);
        var existing = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.DeviceId == device.Id && x.Writable);
        if (existing is not null) return existing.Id;
        var tag = new Tag { DeviceId = device.Id, Name = "AnnRevHorn", Area = TagArea.HoldingRegister, Address = 81, DataType = TagDataType.Bool, Writable = true, Kind = TagKind.Physical };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        return tag.Id;
    }

    [Fact]
    public async Task StaleRevisionConflictThenMatchingSaveSucceeds()
    {
        var horn = await WritableTagAsync();
        var cfg = await _site.ClientAsync("cfgA");
        var body = new
        {
            name = "AnnRev-" + Guid.NewGuid().ToString("N")[..8],
            areaId = _site.AreaA, enabled = true, latchMode = "Follow", silenceMinutes = 5,
            flashOnMs = 400, flashOffMs = 400, criticalFlashOnMs = 200, criticalFlashOffMs = 200, browserSound = true,
            outputs = new object[] { new { kind = "Horn", tagId = horn, activeValue = 1.0, inactiveValue = 0.0, enabled = true } },
            levels = Array.Empty<object>()
        };
        var created = await Json(await cfg.PostAsJsonAsync("/api/annunciators", body));
        var id = created.GetProperty("id").GetInt32();
        var revision = created.GetProperty("revision").GetInt64();

        var stale = new
        {
            body.name, body.areaId, body.enabled, body.latchMode, body.silenceMinutes,
            body.flashOnMs, body.flashOffMs, body.criticalFlashOnMs, body.criticalFlashOffMs, body.browserSound,
            body.outputs, body.levels, revision = revision + 7
        };
        var conflict = await cfg.PutAsJsonAsync($"/api/annunciators/{id}", stale);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var issue = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("重新打开", issue.GetProperty("message").GetString(), StringComparison.Ordinal);

        var ok = await Json(await cfg.PutAsJsonAsync($"/api/annunciators/{id}", new
        {
            body.name, body.areaId, enabled = false, body.latchMode, body.silenceMinutes,
            body.flashOnMs, body.flashOffMs, body.criticalFlashOnMs, body.criticalFlashOffMs, body.browserSound,
            body.outputs, body.levels, revision
        }));
        Assert.False(ok.GetProperty("enabled").GetBoolean());
        Assert.Equal(revision + 1, ok.GetProperty("revision").GetInt64());
        await cfg.DeleteAsync($"/api/annunciators/{id}");
    }
}
