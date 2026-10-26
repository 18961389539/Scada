using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Tests;

public sealed class TraceabilityApiTests(ControlTests.ControlSite site) : IClassFixture<ControlTests.ControlSite>
{
    [Fact]
    public async Task JobsRecordsAndScanBindingsRespectAreaAndClientPermissions()
    {
        using var admin = await site.ClientAsync("admin");
        using var operatorA = await site.ClientAsync("opA");
        using var configA = await site.ClientAsync("cfgA");
        async Task<JsonElement> Ok(HttpResponseMessage response)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        async Task<int> Trigger(int deviceId) => (await Ok(await admin.PostAsJsonAsync("/api/tags", new
        {
            deviceId, name = "trace-trigger-" + Guid.NewGuid().ToString("N"), area = "HoldingRegister", address = 100,
            dataType = "UInt16", scale = 1, offset = 0, writable = true, archiveMode = "None", deadband = 0, archivePeriodSec = 600
        }))).GetProperty("id").GetInt32();
        var triggerA = await Trigger(site.DeviceA);
        var triggerB = await Trigger(site.DeviceB);
        object Input(int trigger, int tag) => new { name = "trace-" + trigger, enabled = true, triggerTagId = trigger, snSource = "WebScan",
            snFormat = "Numeric", snTagIds = Array.Empty<int>(), fields = new[] { new { name = "Current", tagId = tag } }, scanExpirySeconds = 300 };
        var jobA = await Ok(await admin.PostAsJsonAsync("/api/traceability/jobs", Input(triggerA, site.TagA)));
        var jobB = await Ok(await admin.PostAsJsonAsync("/api/traceability/jobs", Input(triggerB, site.TagB)));
        var idA = jobA.GetProperty("id").GetInt32();
        var idB = jobB.GetProperty("id").GetInt32();
        var visible = await operatorA.GetFromJsonAsync<JsonElement[]>("/api/traceability/jobs");
        Assert.Contains(visible!, job => job.GetProperty("id").GetInt32() == idA);
        Assert.DoesNotContain(visible!, job => job.GetProperty("id").GetInt32() == idB);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorA.PostAsJsonAsync($"/api/traceability/jobs/{idB}/scan", new { sn = "FOREIGN" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await configA.PostAsJsonAsync($"/api/traceability/jobs/{idA}/scan", new { sn = "READONLY" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await configA.PostAsJsonAsync("/api/traceability/jobs", Input(triggerA, site.TagA))).StatusCode);
        await Ok(await operatorA.PostAsJsonAsync($"/api/traceability/jobs/{idA}/scan", new { sn = "OWN-SN" }));
        Assert.Equal(HttpStatusCode.Conflict, (await operatorA.PostAsJsonAsync($"/api/traceability/jobs/{idA}/scan", new { sn = "REPLACEMENT" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await operatorA.DeleteAsync($"/api/traceability/jobs/{idA}/scan")).StatusCode);
        var options = site.Services.GetRequiredService<IOptionsMonitor<MobileOptions>>().CurrentValue;
        try
        {
            options.AllowControl = false;
            using var mobile = new HttpRequestMessage(HttpMethod.Post, $"/api/traceability/jobs/{idA}/scan") { Content = JsonContent.Create(new { sn = "MOBILE" }) };
            mobile.Headers.Add(ClientControlPolicy.ClientHeader, "mobile");
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.SendAsync(mobile)).StatusCode);
        }
        finally { options.AllowControl = true; }
        var factory = site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.TraceabilityRecords.AddRange(
                new TraceabilityRecord { JobId = idA, JobName = "A", AreaId = site.AreaA, CycleId = Guid.NewGuid(), Sn = "SHARED", Ts = ScadaTime.NowMs() },
                new TraceabilityRecord { JobId = idB, JobName = "B", AreaId = site.AreaB, CycleId = Guid.NewGuid(), Sn = "SHARED", Ts = ScadaTime.NowMs() });
            await db.SaveChangesAsync();
        }
        var records = await Ok(await operatorA.GetAsync("/api/traceability/records?sn=SHARED"));
        Assert.Equal(1, records.GetProperty("total").GetInt32());
        Assert.Equal(idA, records.GetProperty("items")[0].GetProperty("jobId").GetInt32());
    }
}