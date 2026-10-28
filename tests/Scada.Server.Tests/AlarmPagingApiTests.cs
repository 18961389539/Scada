using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class AlarmPagingApiTests(AuthorizationTests.Site site) : IClassFixture<AuthorizationTests.Site>
{
    private IDbContextFactory<ConfigDbContext> Factory => site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();

    [Fact]
    public async Task LiveFiltersPrecedePagingAndKeepOlderActiveAlarmsBeyondOneThousandReturnedRows()
    {
        using var admin = await site.ClientAsync("admin");
        using var operatorA = await site.ClientAsync("opA");
        var marker = "page-" + Guid.NewGuid().ToString("N");
        var now = ScadaTime.NowMs();
        await using var db = await Factory.CreateDbContextAsync();
        for (var index = 0; index < 1005; index++)
            db.AlarmEvents.Add(new AlarmEvent
            {
                TagId = site.TagA, DeviceId = site.DeviceA, Level = AlarmLevel.Warning,
                Message = marker + " returned", StartTs = now - 60_000 - index, EndTs = now - 100
            });
        var active = Enumerable.Range(0, 3).Select(index => new AlarmEvent
        {
            TagId = site.TagA, DeviceId = site.DeviceA, Level = AlarmLevel.Critical, Message = marker + " Boiler High",
            StartTs = now - (long)TimeSpan.FromDays(3).TotalMilliseconds + index
        }).ToArray();
        db.AlarmEvents.AddRange(active);
        db.AlarmEvents.Add(new AlarmEvent
        {
            TagId = site.TagB, DeviceId = site.DeviceB, Level = AlarmLevel.Critical,
            Message = marker + " Boiler High", StartTs = now
        });
        await db.SaveChangesAsync();

        var filter = $"live=true&state=active&level=Critical&keyword={marker.ToUpperInvariant()}%20BOILER%20high&limit=1";
        var first = await operatorA.GetFromJsonAsync<JsonElement>("/api/alarms/page?" + filter);
        Assert.Equal(3, first.GetProperty("total").GetInt32());
        Assert.Equal(active[2].Id, Assert.Single(first.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
        var second = await operatorA.GetFromJsonAsync<JsonElement>("/api/alarms/page?" + filter + "&offset=1");
        Assert.Equal(active[1].Id, Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt32());
        var allAreas = await admin.GetFromJsonAsync<JsonElement>("/api/alarms/page?" + filter);
        Assert.Equal(4, allAreas.GetProperty("total").GetInt32());
        var hiddenTag = await operatorA.GetFromJsonAsync<JsonElement>($"/api/alarms/page?{filter}&tagId={site.TagB}");
        Assert.Equal(0, hiddenTag.GetProperty("total").GetInt32());
        Assert.Empty(hiddenTag.GetProperty("items").EnumerateArray());

        var returned = await operatorA.GetFromJsonAsync<JsonElement>($"/api/alarms/page?live=true&state=rtnUnack&keyword={marker}&offset=1000&limit=100");
        Assert.Equal(1005, returned.GetProperty("total").GetInt32());
        Assert.Equal(5, returned.GetProperty("items").GetArrayLength());
        Assert.False(returned.GetProperty("capped").GetBoolean());
    }

    [Fact]
    public async Task VisibilityAndSummaryAreNotTruncatedByFiveThousandNewerHiddenAlarms()
    {
        using var admin = await site.ClientAsync("admin");
        using var operatorA = await site.ClientAsync("opA");
        var beforeAdmin = await admin.GetFromJsonAsync<JsonElement>("/api/alarms/summary");
        var beforeOperator = await operatorA.GetFromJsonAsync<JsonElement>("/api/alarms/summary");
        var marker = "scope-" + Guid.NewGuid().ToString("N");
        var now = ScadaTime.NowMs();
        await using var db = await Factory.CreateDbContextAsync();
        for (var index = 0; index < 5001; index++)
            db.AlarmEvents.Add(new AlarmEvent
            {
                TagId = site.TagB, DeviceId = site.DeviceB, Level = AlarmLevel.Warning,
                Message = marker, StartTs = now - index
            });
        var visible = new[]
        {
            new AlarmEvent { TagId = site.TagA, DeviceId = site.DeviceA, Message = marker, StartTs = now - 60_001 },
            new AlarmEvent { DeviceId = site.DeviceA, Message = marker, StartTs = now - 60_002 },
            new AlarmEvent { Message = marker, StartTs = now - 60_003 },
            new AlarmEvent { TagId = site.TagA, DeviceId = site.DeviceB, Message = marker, StartTs = now - 60_004 }
        };
        db.AlarmEvents.AddRange(visible);
        db.AlarmEvents.Add(new AlarmEvent { TagId = site.TagB, DeviceId = site.DeviceA, Message = marker, StartTs = now });
        await db.SaveChangesAsync();

        var page = await operatorA.GetFromJsonAsync<JsonElement>($"/api/alarms/page?keyword={marker}&offset=1&limit=2");
        Assert.Equal(4, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.False(page.GetProperty("capped").GetBoolean());
        Assert.All(page.GetProperty("items").EnumerateArray(), item =>
            Assert.Contains(item.GetProperty("id").GetInt32(), visible.Select(alarm => alarm.Id)));
        var summaryAdmin = await admin.GetFromJsonAsync<JsonElement>("/api/alarms/summary");
        var summaryOperator = await operatorA.GetFromJsonAsync<JsonElement>("/api/alarms/summary");
        Assert.Equal(beforeAdmin.GetProperty("unack").GetInt32() + 5006, summaryAdmin.GetProperty("unack").GetInt32());
        Assert.Equal(beforeOperator.GetProperty("unack").GetInt32() + 4, summaryOperator.GetProperty("unack").GetInt32());
    }

    [Fact]
    public async Task StateFiltersMatchTheLiveLifecycleAndHistoricalFiltersApplyBeforeCounting()
    {
        using var admin = await site.ClientAsync("admin");
        var marker = "states-" + Guid.NewGuid().ToString("N");
        var now = ScadaTime.NowMs();
        AlarmEvent Item(long age) => new()
        {
            TagId = site.TagA, DeviceId = site.DeviceA, Level = AlarmLevel.Warning, Message = marker, StartTs = now - age
        };
        var unack = Item(1000);
        var acked = Item(2000); acked.AckTs = now - 500;
        var returned = Item(3000); returned.EndTs = now - 2000;
        var shelved = Item(4000); shelved.Shelved = true;
        var suppressed = Item(5000); suppressed.Suppressed = true; suppressed.AckTs = now - 500;
        var old = Item((long)TimeSpan.FromDays(10).TotalMilliseconds); old.EndTs = now - (long)TimeSpan.FromDays(8).TotalMilliseconds;
        var pulse = Item(6000); pulse.EndTs = pulse.StartTs;
        var normal = Item(8000); normal.EndTs = now - 7000; normal.AckTs = now - 6500;
        await using var db = await Factory.CreateDbContextAsync();
        db.AlarmEvents.AddRange(unack, acked, returned, shelved, suppressed, old, pulse, normal);
        await db.SaveChangesAsync();
        foreach (var (state, expected) in new[]
        {
            ("active", new[] { unack.Id, acked.Id }), ("unack", new[] { unack.Id }),
            ("acked", new[] { acked.Id }), ("rtnUnack", new[] { returned.Id }),
            ("shelved", new[] { shelved.Id }), ("suppressed", new[] { suppressed.Id })
        })
        {
            var page = await admin.GetFromJsonAsync<JsonElement>($"/api/alarms/page?live=true&state={state}&keyword={marker}");
            Assert.Equal(expected.Length, page.GetProperty("total").GetInt32());
            Assert.Equal(expected.Order(), page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).Order());
        }
        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/alarms/page?from={now - (long)TimeSpan.FromDays(11).TotalMilliseconds}&to={now}&keyword={marker}&level=Warning&tagId={site.TagA}&limit=2");
        Assert.Equal(8, history.GetProperty("total").GetInt32());
        Assert.Equal(2, history.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AckableSummaryIntersectsViewAndAcknowledgmentPermissionsFromSeparateGrants()
    {
        using var admin = await site.ClientAsync("admin");
        using var operatorA = await site.ClientAsync("opA");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var db = await Factory.CreateDbContextAsync();
        db.AlarmEvents.AddRange(
            new AlarmEvent { TagId = site.TagA, DeviceId = site.DeviceA, Message = suffix, StartTs = ScadaTime.NowMs() },
            new AlarmEvent { TagId = site.TagB, DeviceId = site.DeviceB, Message = suffix, StartTs = ScadaTime.NowMs() },
            new AlarmEvent { Message = suffix, StartTs = ScadaTime.NowMs() });
        await db.SaveChangesAsync();
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/alarms/summary");
        var areaA = await operatorA.GetFromJsonAsync<JsonElement>("/api/alarms/summary");
        var system = await db.AlarmEvents.CountAsync(item => item.TagId == null && item.DeviceId == null &&
            item.EndTs == null && item.AckTs == null && !item.Shelved && !item.Suppressed);
        async Task<int> Create(string path, object body)
        {
            using var response = await admin.PostAsJsonAsync(path, body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }
        var viewRole = await Create("/api/roles", new { name = "page-view-" + suffix, permissions = new[] { "View" } });
        var ackRole = await Create("/api/roles", new { name = "page-ack-" + suffix, permissions = new[] { "AckAlarm" } });
        var countA = areaA.GetProperty("unack").GetInt32();
        (int? ViewArea, int? AckArea, int Unack, int Ackable)[] cases =
        [
            (site.AreaA, null, countA, countA),
            (null, site.AreaA, all.GetProperty("unack").GetInt32(), countA),
            (site.AreaA, site.AreaB, countA, system)
        ];
        foreach (var (index, permissions) in cases.Index())
        {
            var userName = $"page-{suffix}-{index}";
            var userId = await Create("/api/users", new { userName, password = "secret123", displayName = userName, role = "None", enabled = true });
            using var assigned = await admin.PutAsJsonAsync($"/api/users/{userId}/assignments", new
            {
                assignments = new[] { new { roleId = viewRole, areaId = permissions.ViewArea }, new { roleId = ackRole, areaId = permissions.AckArea } }
            });
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
            using var client = await site.ClientAsync(userName);
            var summary = await client.GetFromJsonAsync<JsonElement>("/api/alarms/summary");
            Assert.Equal(permissions.Unack, summary.GetProperty("unack").GetInt32());
            Assert.Equal(permissions.Ackable, summary.GetProperty("ackableUnack").GetInt32());
        }
    }

    [Fact]
    public async Task EqualTimestampsHaveStablePageBoundariesAndInvalidFiltersAreRejected()
    {
        using var admin = await site.ClientAsync("admin");
        var marker = "ties-" + Guid.NewGuid().ToString("N");
        var now = ScadaTime.NowMs();
        await using var db = await Factory.CreateDbContextAsync();
        var events = Enumerable.Range(0, 4).Select(_ => new AlarmEvent { Message = marker, StartTs = now }).ToArray();
        db.AlarmEvents.AddRange(events);
        await db.SaveChangesAsync();
        var ids = new List<int>();
        foreach (var offset in new[] { 0, 2 })
        {
            var page = await admin.GetFromJsonAsync<JsonElement>($"/api/alarms/page?live=true&keyword={marker}&offset={offset}&limit=2");
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));
        }
        Assert.Equal(events.Select(item => item.Id).OrderDescending(), ids);
        foreach (var query in new[] { "state=invalid", "state=999", "level=999", "tagId=0", "from=2&to=1" })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/alarms/page?" + query)).StatusCode);
        admin.DefaultRequestHeaders.Add("X-Scada-Locale", "en-US");
        Assert.Contains("Invalid alarm state filter", await (await admin.GetAsync("/api/alarms/page?state=invalid")).Content.ReadAsStringAsync());
    }
}
