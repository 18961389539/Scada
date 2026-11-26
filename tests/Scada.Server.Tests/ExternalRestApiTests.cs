using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class ExternalRestApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public ExternalRestApiTests(AuthorizationTests.Site site) => _site = site;

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<(HttpClient Client, string Secret, int KeyId)> CreateKeyAsync(params string[] scopes)
    {
        var admin = await _site.ClientAsync("admin");
        var created = await Json(await admin.PostAsJsonAsync("/api/external/keys", new
        {
            name = "rest-" + Guid.NewGuid().ToString("N")[..8],
            scopes,
            areaIds = new[] { _site.AreaA },
            rateLimitPerMinute = 1000,
            notes = "test"
        }));
        var secret = created.GetProperty("secret").GetString()!;
        var id = created.GetProperty("key").GetProperty("id").GetInt32();
        var client = _site.Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return (client, secret, id);
    }

    [Fact]
    public async Task ApiKey_ReadTags_Realtime_History_Alarms_And_WriteThroughControl()
    {
        var (client, _, keyId) = await CreateKeyAsync("ReadTags", "ReadHistory", "ReadAlarms", "Write");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health")).StatusCode);

        var tags = await Json(await client.GetAsync("/api/v1/tags"));
        Assert.Contains(tags.EnumerateArray(), t => t.GetProperty("id").GetInt32() == _site.TagA);
        Assert.DoesNotContain(tags.EnumerateArray(), t => t.GetProperty("id").GetInt32() == _site.TagB);

        var cache = _site.Services.GetRequiredService<IRealtimeCache>();
        cache.Set(new TagValue(_site.TagA, 12.5, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));
        cache.Set(new TagValue(_site.TagB, 99, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));
        var values = await Json(await client.PostAsJsonAsync("/api/v1/realtime/batch", new { tagIds = new[] { _site.TagA, _site.TagB } }));
        Assert.Single(values.EnumerateArray());
        Assert.Equal(_site.TagA, values[0].GetProperty("tagId").GetInt32());
        Assert.False(values[0].GetProperty("simulated").GetBoolean());
        cache.Set(new TagValue(_site.TagA, 12.5, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs(), Simulated: true));
        var simulated = await Json(await client.PostAsJsonAsync("/api/v1/realtime/batch", new { tagIds = new[] { _site.TagA } }));
        Assert.True(simulated[0].GetProperty("simulated").GetBoolean());
        Assert.Equal("Good", simulated[0].GetProperty("quality").GetString());
        cache.Set(new TagValue(_site.TagA, 12.5, Scada.Core.Enums.TagQuality.Good, ScadaTime.NowMs()));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/history?tagIds={_site.TagA}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/history?tagIds={_site.TagB}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/alarms?activeOnly=true&limit=10")).StatusCode);

        var write = await Json(await client.PostAsJsonAsync("/api/v1/commands", new { tagId = _site.TagA, value = 1, confirmed = true, requestId = Guid.NewGuid() }));
        Assert.True(write.TryGetProperty("success", out _));
        var missingId = await client.PostAsJsonAsync("/api/v1/commands", new { tagId = _site.TagA, value = 1, confirmed = true });
        Assert.Equal(HttpStatusCode.BadRequest, missingId.StatusCode);
        Assert.Contains("请提供命令号", await missingId.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/commands", new { tagId = _site.TagB, value = 1, confirmed = true, requestId = Guid.NewGuid() })).StatusCode);

        var admin = await _site.ClientAsync("admin");
        await Json(await admin.PostAsJsonAsync($"/api/external/keys/{keyId}/revoke", new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/health")).StatusCode);
    }

    [Fact]
    public async Task ApiKey_AlarmListFillsLimitWhenNewerAlarmsAreOutsideItsArea()
    {
        var (client, _, keyId) = await CreateKeyAsync("ReadAlarms");
        var factory = _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();
        var to = ScadaTime.NowMs() - (long)TimeSpan.FromDays(9).TotalMilliseconds;
        var from = to - 60_000;
        AlarmEvent[] visible;
        await using (var db = await factory.CreateDbContextAsync())
        {
            // 隐藏区域的新报警排满旧实现的 4 倍预取窗口，确保可见报警不会因取数阶段被截断而丢失。
            for (var index = 0; index < 20; index++)
                db.AlarmEvents.Add(new AlarmEvent
                {
                    TagId = _site.TagB, DeviceId = _site.DeviceB, Level = AlarmLevel.Warning,
                    Message = "hidden", StartTs = to - index
                });
            visible = Enumerable.Range(0, 5).Select(index => new AlarmEvent
            {
                TagId = _site.TagA, DeviceId = _site.DeviceA, Level = AlarmLevel.Critical,
                Message = "visible", StartTs = from + index
            }).ToArray();
            db.AlarmEvents.AddRange(visible);
            await db.SaveChangesAsync();
        }

        var rows = await Json(await client.GetAsync($"/api/v1/alarms?activeOnly=true&from={from}&to={to}&limit=5"));

        Assert.Equal(5, rows.GetArrayLength());
        Assert.All(rows.EnumerateArray(), row => Assert.Contains(row.GetProperty("id").GetInt32(), visible.Select(alarm => alarm.Id)));
        var admin = await _site.ClientAsync("admin");
        await Json(await admin.PostAsJsonAsync($"/api/external/keys/{keyId}/revoke", new { }));
    }

    [Fact]
    public async Task ApiKey_MissingScope_IsForbidden_JwtStillWorks()
    {
        var (client, _, keyId) = await CreateKeyAsync("ReadTags");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/alarms?activeOnly=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/commands", new { tagId = _site.TagA, value = 1, confirmed = true, requestId = Guid.NewGuid() })).StatusCode);

        var jwt = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.OK, (await jwt.GetAsync("/api/v1/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await jwt.GetAsync("/api/v1/alarms?activeOnly=true&limit=5")).StatusCode);

        var admin = await _site.ClientAsync("admin");
        await Json(await admin.PostAsJsonAsync($"/api/external/keys/{keyId}/revoke", new { }));
    }

    [Fact]
    public async Task BearerApiKey_Header_Works_OpenApi_Anonymous()
    {
        var (c, secret, keyId) = await CreateKeyAsync("ReadTags");
        var client = _site.Server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health")).StatusCode);

        var anon = _site.Server.CreateClient();
        var openapi = await anon.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openapi.StatusCode);
        var doc = await openapi.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/health", doc);

        var admin = await _site.ClientAsync("admin");
        await Json(await admin.PostAsJsonAsync($"/api/external/keys/{keyId}/revoke", new { }));
        _ = c;
    }

    [Fact]
    public async Task CreatingKey_RequiresSystem_And_Audits()
    {
        var op = await _site.ClientAsync("opA");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/external/keys", new { name = "x", scopes = new[] { "ReadTags" } })).StatusCode);
        var admin = await _site.ClientAsync("admin");
        var created = await Json(await admin.PostAsJsonAsync("/api/external/keys", new { name = "sys-" + Guid.NewGuid().ToString("N")[..6], scopes = new[] { "ReadTags" } }));
        var id = created.GetProperty("key").GetProperty("id").GetInt32();
        var log = await _site.LastLogAsync(x => x.TargetType == "ExternalApiKey" && x.TargetId == id.ToString());
        Assert.NotNull(log);
        await Json(await admin.PostAsJsonAsync($"/api/external/keys/{id}/revoke", new { }));
    }
}
