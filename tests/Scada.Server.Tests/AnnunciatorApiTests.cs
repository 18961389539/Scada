using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;
using Scada.Engine.Alarms;

namespace Scada.Server.Tests;

public sealed class AnnunciatorApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public AnnunciatorApiTests(AuthorizationTests.Site site) => _site = site;
    private IDbContextFactory<ConfigDbContext> Factory() => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static async Task<JsonElement> Json(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync()); return await r.Content.ReadFromJsonAsync<JsonElement>(); }

    private async Task<(int TagHorn, int TagRed, int DeviceId)> EnsureWritableTagsAsync()
    {
        await using var db = await Factory().CreateDbContextAsync();
        var device = await db.Devices.AsNoTracking().FirstAsync(x => x.AreaId == _site.AreaA);
        async Task<int> Tag(string name)
        {
            var existing = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.DeviceId == device.Id && x.Name == name);
            if (existing is not null) return existing.Id;
            var tag = new Tag { DeviceId = device.Id, Name = name, Area = TagArea.HoldingRegister, Address = Math.Abs(name.GetHashCode() % 200) + 20, DataType = TagDataType.Bool, Writable = true, Kind = TagKind.Physical };
            db.Tags.Add(tag); await db.SaveChangesAsync(); return tag.Id;
        }
        return (await Tag("AnnHorn"), await Tag("AnnRed"), device.Id);
    }

    [Fact]
    public async Task ConfigRequiresAreaConfigure_SilenceNeedsAck_AreaIsolation()
    {
        var (horn, red, _) = await EnsureWritableTagsAsync();
        var name = "Ann-" + Guid.NewGuid().ToString("N")[..8];
        var body = new
        {
            name, areaId = _site.AreaA, enabled = true, latchMode = "Follow", silenceMinutes = 5,
            flashOnMs = 400, flashOffMs = 400, criticalFlashOnMs = 200, criticalFlashOffMs = 200, browserSound = true,
            outputs = new object[]
            {
                new { kind = "Horn", tagId = horn, activeValue = 1.0, inactiveValue = 0.0, enabled = true },
                new { kind = "TowerRed", tagId = red, activeValue = 1.0, inactiveValue = 0.0, enabled = true }
            },
            levels = new object[]
            {
                new { level = "Critical", horn = true, beacon = false, towerRed = true, towerYellow = false, towerBlue = false, towerGreen = false, flash = true, soundKey = "critical" },
                new { level = "Warning", horn = true, beacon = true, towerRed = false, towerYellow = true, towerBlue = false, towerGreen = false, flash = true, soundKey = "warning" },
                new { level = "Info", horn = false, beacon = true, towerRed = false, towerYellow = false, towerBlue = true, towerGreen = false, flash = false, soundKey = "info" }
            }
        };
        var op = await _site.ClientAsync("opA");
        var cfg = await _site.ClientAsync("cfgA");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/annunciators", body)).StatusCode);
        var created = await Json(await cfg.PostAsJsonAsync("/api/annunciators", body));
        var id = created.GetProperty("id").GetInt32();
        var otherArea = new
        {
            name = name + "-B", areaId = _site.AreaB, enabled = true, latchMode = "Follow", silenceMinutes = 5,
            flashOnMs = 400, flashOffMs = 400, criticalFlashOnMs = 200, criticalFlashOffMs = 200, browserSound = true,
            outputs = body.outputs, levels = body.levels
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/annunciators", otherArea)).StatusCode);

        var listOp = await Json(await op.GetAsync("/api/annunciators"));
        Assert.Contains(listOp.EnumerateArray(), x => x.GetProperty("id").GetInt32() == id);
        // cfgA 有配置无确认权限，不能消音。
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync($"/api/annunciators/{id}/silence", new { minutes = 2 })).StatusCode);
        var silenced = await Json(await op.PostAsJsonAsync($"/api/annunciators/{id}/silence", new { minutes = 2, reason = "巡检" }));
        Assert.True(silenced.GetProperty("silenced").GetBoolean());

        // 写入活动未确认报警后刷新，消音应压制喇叭。
        await using (var db = await Factory().CreateDbContextAsync())
        {
            var tag = await db.Tags.AsNoTracking().FirstAsync(x => x.Id == horn);
            db.AlarmEvents.Add(new AlarmEvent
            {
                TagId = tag.Id, DeviceId = tag.DeviceId, Level = AlarmLevel.Critical, Value = 1, StartTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Message = "annunciator-test"
            });
            await db.SaveChangesAsync();
        }
        _site.Services.GetRequiredService<AnnunciatorService>().RequestReconcile();
        await Task.Delay(300);
        var status = await Json(await op.GetAsync("/api/annunciators/status"));
        var mine = status.EnumerateArray().Single(x => x.GetProperty("panelId").GetInt32() == id);
        Assert.True(mine.GetProperty("silenced").GetBoolean());
        Assert.False(mine.GetProperty("horn").GetBoolean());

        await Json(await cfg.DeleteAsync($"/api/annunciators/{id}"));
    }

    [Fact]
    public async Task GlobalPanelNeedsGlobalConfigure()
    {
        var (horn, red, _) = await EnsureWritableTagsAsync();
        var cfg = await _site.ClientAsync("cfgA");
        var admin = await _site.ClientAsync("admin");
        var body = new
        {
            name = "GlobalAnn-" + Guid.NewGuid().ToString("N")[..6], areaId = (int?)null, enabled = true, latchMode = "Follow", silenceMinutes = 3,
            flashOnMs = 500, flashOffMs = 500, criticalFlashOnMs = 250, criticalFlashOffMs = 250, browserSound = true,
            outputs = new object[] { new { kind = "Horn", tagId = horn, activeValue = 1.0, inactiveValue = 0.0, enabled = true } },
            levels = Array.Empty<object>()
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await cfg.PostAsJsonAsync("/api/annunciators", body)).StatusCode);
        var created = await Json(await admin.PostAsJsonAsync("/api/annunciators", body));
        await Json(await admin.DeleteAsync($"/api/annunciators/{created.GetProperty("id").GetInt32()}"));
        _ = red;
    }
}
