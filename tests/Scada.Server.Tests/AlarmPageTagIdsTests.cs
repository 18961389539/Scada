using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class AlarmPageTagIdsTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public AlarmPageTagIdsTests(AuthorizationTests.Site site) => _site = site;

    [Fact]
    public async Task PageAcceptsCommaSeparatedTagIds()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using (var runtime = await _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>().CreateDbContextAsync())
        {
            runtime.AlarmEvents.AddRange(
                new AlarmEvent { TagId = _site.TagA, Level = AlarmLevel.Warning, Value = 1, StartTs = now - 1000, Message = "page-tag-a" },
                new AlarmEvent { TagId = _site.TagB, Level = AlarmLevel.Warning, Value = 1, StartTs = now - 500, Message = "page-tag-b" });
            await runtime.SaveChangesAsync();
        }

        var admin = await _site.ClientAsync("admin");
        var both = await (await admin.GetAsync($"/api/alarms/page?live=true&tagIds={_site.TagA},{_site.TagB}&limit=50")).Content.ReadFromJsonAsync<JsonElement>();
        var messages = both.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("message").GetString()).ToList();
        Assert.Contains("page-tag-a", messages);
        Assert.Contains("page-tag-b", messages);

        var single = await (await admin.GetAsync($"/api/alarms/page?live=true&tagIds={_site.TagA}&limit=50")).Content.ReadFromJsonAsync<JsonElement>();
        var singleMessages = single.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("message").GetString()).ToList();
        Assert.Contains("page-tag-a", singleMessages);
        Assert.DoesNotContain("page-tag-b", singleMessages);
    }
}
