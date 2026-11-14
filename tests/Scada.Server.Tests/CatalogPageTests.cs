using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Scada.Server.Tests;

public sealed class CatalogPageTests(AuthorizationTests.Site site) : IClassFixture<AuthorizationTests.Site>
{
    [Fact]
    public async Task DevicePageFiltersInTheDatabaseAndKeepsTheUnpagedList()
    {
        using var admin = await site.ClientAsync("admin");
        var name = "Case" + Guid.NewGuid().ToString("N");
        var other = "page-" + Guid.NewGuid().ToString("N");
        await CreateDevice(admin, name, site.AreaA);
        await CreateDevice(admin, other, site.AreaA);

        using var page = await admin.GetAsync("/api/devices?take=1&skip=0");
        page.EnsureSuccessStatusCode();
        var total = Count(page);
        var first = await page.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.True(total > 1);
        Assert.Single(first!);

        using var next = await admin.GetAsync("/api/devices?take=1&skip=1");
        next.EnsureSuccessStatusCode();
        Assert.Equal(total, Count(next));
        var second = Assert.Single(await next.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.NotEqual(first![0].GetProperty("id").GetInt32(), second.GetProperty("id").GetInt32());

        using var found = await admin.GetAsync("/api/devices?take=20&search=" + Uri.EscapeDataString(name));
        found.EnsureSuccessStatusCode();
        Assert.Equal(1, Count(found));
        var match = Assert.Single(await found.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
        Assert.Equal(name, match.GetProperty("name").GetString());

        using var wrongCase = await admin.GetAsync("/api/devices?take=20&search=" + Uri.EscapeDataString(name.ToLowerInvariant()));
        wrongCase.EnsureSuccessStatusCode();
        Assert.Equal(0, Count(wrongCase));
        Assert.Empty(await wrongCase.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);

        using var sorted = await admin.GetAsync("/api/devices?take=2&sort=name&order=descending&protocol=ModbusTcp&enabled=disabled");
        sorted.EnsureSuccessStatusCode();
        Assert.True(Count(sorted) > 1);
        var sortedItems = await sorted.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
        Assert.Equal(2, sortedItems.Length);
        Assert.True(string.CompareOrdinal(sortedItems[0].GetProperty("name").GetString(), sortedItems[1].GetProperty("name").GetString()) >= 0);

        using var protocols = await admin.GetAsync("/api/devices?take=1");
        protocols.EnsureSuccessStatusCode();
        var names = Header(protocols, "X-Device-Protocols").Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("ModbusTcp", names);
        Assert.Equal(names.OrderBy(item => item, StringComparer.Ordinal), names);

        using var all = await admin.GetAsync("/api/devices");
        all.EnsureSuccessStatusCode();
        Assert.False(all.Headers.Contains("X-Total-Count") || all.Content.Headers.Contains("X-Total-Count"));
        var listed = await all.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
        Assert.Equal(total, listed.Length);
        Assert.Contains(listed, item => item.GetProperty("name").GetString() == name);
        Assert.Contains(listed, item => item.GetProperty("name").GetString() == other);
    }

    [Fact]
    public async Task DevicePageHidesDevicesOutsideTheCallerAreas()
    {
        using var admin = await site.ClientAsync("admin");
        using var areaUser = await site.ClientAsync("opA");
        var hidden = "hidden-" + Guid.NewGuid().ToString("N");
        await CreateDevice(admin, hidden, site.AreaB);

        using var adminPage = await admin.GetAsync("/api/devices?take=1&search=" + Uri.EscapeDataString(hidden));
        using var areaPage = await areaUser.GetAsync("/api/devices?take=1&search=" + Uri.EscapeDataString(hidden));
        adminPage.EnsureSuccessStatusCode();
        areaPage.EnsureSuccessStatusCode();
        Assert.Equal(1, Count(adminPage));
        Assert.Equal(0, Count(areaPage));
        Assert.Empty(await areaPage.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);

        using var adminAll = await admin.GetAsync("/api/devices?take=1");
        using var areaAll = await areaUser.GetAsync("/api/devices?take=1");
        Assert.True(Count(areaAll) < Count(adminAll));
    }

    [Fact]
    public async Task AlarmRulePageReturnsOneRuleAndKeepsTheUnpagedList()
    {
        using var admin = await site.ClientAsync("admin");
        var firstMessage = "rule-" + Guid.NewGuid().ToString("N");
        var secondMessage = "rule-" + Guid.NewGuid().ToString("N");
        await CreateRule(admin, site.TagA, firstMessage, 10);
        await CreateRule(admin, site.TagA, secondMessage, 20);

        using var page = await admin.GetAsync("/api/alarm-rules?take=1&skip=0");
        page.EnsureSuccessStatusCode();
        var total = Count(page);
        Assert.True(total > 1);
        Assert.Single(await page.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);

        using var byTag = await admin.GetAsync("/api/alarm-rules?take=2&sort=tagName&order=ascending");
        byTag.EnsureSuccessStatusCode();
        Assert.Equal(2, (await byTag.Content.ReadFromJsonAsync<JsonElement[]>() ?? []).Length);
        using var byMessage = await admin.GetAsync("/api/alarm-rules?take=2&sort=message&order=descending&state=enabled&category=Process");
        byMessage.EnsureSuccessStatusCode();
        var messages = await byMessage.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
        Assert.Equal(2, messages.Length);
        Assert.True(string.CompareOrdinal(messages[0].GetProperty("message").GetString(), messages[1].GetProperty("message").GetString()) >= 0);

        using var found = await admin.GetAsync("/api/alarm-rules?take=10&search=" + Uri.EscapeDataString(firstMessage));
        found.EnsureSuccessStatusCode();
        Assert.Equal(1, Count(found));
        Assert.Equal(firstMessage, Assert.Single(await found.Content.ReadFromJsonAsync<JsonElement[]>() ?? []).GetProperty("message").GetString());

        using var all = await admin.GetAsync("/api/alarm-rules");
        all.EnsureSuccessStatusCode();
        var listed = await all.Content.ReadFromJsonAsync<JsonElement[]>() ?? [];
        Assert.Equal(total, listed.Length);
        Assert.Contains(listed, item => item.GetProperty("message").GetString() == firstMessage);
        Assert.Contains(listed, item => item.GetProperty("message").GetString() == secondMessage);
    }

    private static async Task CreateDevice(HttpClient admin, string name, int area)
    {
        using var response = await admin.PostAsJsonAsync("/api/devices", AuthorizationTests.Site.DeviceRequest(name, area));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task CreateRule(HttpClient admin, int tagId, string message, double threshold)
    {
        using var response = await admin.PostAsJsonAsync("/api/alarm-rules", new
        {
            tagId,
            type = "High",
            threshold,
            level = "Warning",
            delaySec = 0,
            recoverDeadband = 0,
            enabled = true,
            message
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
            return values.Single();
        throw new InvalidOperationException($"响应缺少 {name}");
    }

    private static int Count(HttpResponseMessage response) => int.Parse(Header(response, "X-Total-Count"));
}
