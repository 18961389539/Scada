using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Scada.Core.Options;

namespace Scada.Server.Tests;

public sealed class SiteInfoTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicIdentityIsTrimmedExplicitAndContainsNoOperationalConfiguration(bool demoMode)
    {
        var site = new IdentitySite(demoMode);
        try
        {
            await site.InitializeAsync();
            using var client = site.CreateClient();
            using var response = await client.GetAsync("/api/auth/site-info");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var info = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Assembly Hall", info.GetProperty("siteName").GetString());
            Assert.Equal(demoMode, info.GetProperty("demoMode").GetBoolean());
            Assert.Equal(["demoMode", "siteName"], info.EnumerateObject().Select(property => property.Name).Order());
        }
        finally { await site.DisposeAsync(); }
    }

    [Fact]
    public void DemoConveniencesAreNotEnabledByDataSeeding()
    {
        Assert.False(new ScadaSettings { SeedDemoData = true }.DemoMode);
    }

    private sealed class IdentitySite(bool demoMode) : AuthorizationTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder builder)
        {
            builder.UseSetting("Scada:SiteName", "  Assembly Hall  ");
            builder.UseSetting("Scada:DemoMode", demoMode.ToString());
        }
    }
}
