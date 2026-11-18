using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class ControlConfirmationTests : IClassFixture<ControlConfirmationTests.ConfirmationSite>
{
    public sealed class ConfirmationSite : ControlTests.ControlSite
    {
        protected override void ConfigureSettings(IWebHostBuilder b) => b.UseSetting("Scada:ControlRequireConfirmByDefault", "true");
    }

    private readonly ConfirmationSite _site;
    public ControlConfirmationTests(ConfirmationSite site) => _site = site;

    private IDbContextFactory<RuntimeDbContext> Runtime() => _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();
    private ControlTests.FakeCollector Collector => _site.Services.GetRequiredService<ControlTests.FakeCollector>();
    private static string U() => Guid.NewGuid().ToString("N")[..6];

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> TagAsync(HttpClient admin, string name) =>
        (await Body(await admin.PostAsJsonAsync("/api/tags", new
        {
            deviceId = _site.DeviceA, name = name + U(), area = "HoldingRegister", address = 0, dataType = "Float",
            scale = 1.0, offset = 0.0, writable = true, archiveMode = "None", deadband = 0.0, archivePeriodSec = 600
        }))).GetProperty("id").GetInt32();

    private async Task<int> TextTagAsync(HttpClient admin, string name) =>
        (await Body(await admin.PostAsJsonAsync("/api/tags", new
        {
            deviceId = _site.DeviceA, name = name + U(), area = "HoldingRegister", address = 100, dataType = "String",
            textLength = 16, scale = 1.0, offset = 0.0, writable = true, archiveMode = "None", deadband = 0.0, archivePeriodSec = 600
        }))).GetProperty("id").GetInt32();

    [Fact]
    public async Task ConfirmedFlagAloneIsRejected()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var tag = await TagAsync(admin, "裸确认");
        var result = await Body(await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true }));
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("检查接口", result.GetProperty("message").GetString());
        Assert.Equal(0, Collector.Count(tag));
    }

    [Fact]
    public async Task CheckIssuesSingleUseGrant()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var tag = await TagAsync(admin, "一次性确认");
        var check = await Body(await op.PostAsJsonAsync("/api/control/check", new { tagId = tag, value = 1 }));
        Assert.Empty(check.GetProperty("refusals").EnumerateArray());
        var first = await Body(await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true }));
        Assert.True(first.GetProperty("success").GetBoolean(), first.ToString());
        var second = await Body(await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 1, requestId = Guid.NewGuid(), confirmed = true }));
        Assert.False(second.GetProperty("success").GetBoolean());
        Assert.Contains("检查接口", second.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CheckIsBoundToUserAndTargetValue()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA"); var other = await _site.ClientAsync("admin");
        var tag = await TagAsync(admin, "绑定确认");
        await Body(await op.PostAsJsonAsync("/api/control/check", new { tagId = tag, value = 3 }));
        var wrongValue = await Body(await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 4, requestId = Guid.NewGuid(), confirmed = true }));
        Assert.False(wrongValue.GetProperty("success").GetBoolean());
        var otherUser = await Body(await other.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 3, requestId = Guid.NewGuid(), confirmed = true }));
        Assert.False(otherUser.GetProperty("success").GetBoolean());
        var own = await Body(await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 3, requestId = Guid.NewGuid(), confirmed = true }));
        Assert.True(own.GetProperty("success").GetBoolean(), own.ToString());
    }

    [Fact]
    public async Task TextWriteRequiresCheckText()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var tag = await TextTagAsync(admin, "文本");
        var direct = await Body(await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 0, requestId = Guid.NewGuid(), confirmed = true, text = "LOT-1" }));
        Assert.False(direct.GetProperty("success").GetBoolean());
        Assert.Contains("检查接口", direct.GetProperty("message").GetString());
        var check = await Body(await op.PostAsJsonAsync("/api/control/check-text", new { tagId = tag, text = "LOT-1" }));
        Assert.Empty(check.GetProperty("refusals").EnumerateArray());
        Assert.True(check.GetProperty("requireConfirm").GetBoolean());
        var wrongText = await Body(await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 0, requestId = Guid.NewGuid(), confirmed = true, text = "LOT-2" }));
        Assert.False(wrongText.GetProperty("success").GetBoolean());
        var ok = await Body(await op.PostAsJsonAsync("/api/commands", new { tagId = tag, value = 0, requestId = Guid.NewGuid(), confirmed = true, text = "LOT-1" }));
        Assert.DoesNotContain("操作确认", ok.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TextCheckEnforcesPermitExpression()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var tag = await TextTagAsync(admin, "文本互锁"); var permit = await TagAsync(admin, "文本许可");
        await Body(await admin.PutAsJsonAsync($"/api/control/points/{tag}", new
        {
            requireConfirm = true, dualApproval = false, approvalTimeoutSec = 300, permitExpression = $"#{permit} > 0",
            permitMessage = "批次未放行", limitMin = (double?)null, limitMax = (double?)null, maxStep = (double?)null,
            feedbackExpression = (string?)null, feedbackTimeoutMs = 10000
        }));
        var cache = _site.Services.GetRequiredService<Scada.Core.Interfaces.IRealtimeCache>();
        cache.Set(new TagValue(permit, 0, TagQuality.Good, ScadaTime.NowMs()));
        var check = await Body(await op.PostAsJsonAsync("/api/control/check-text", new { tagId = tag, text = "LOT-9" }));
        Assert.Contains(check.GetProperty("refusals").EnumerateArray(), x => x.GetString()!.Contains("批次未放行"));
        cache.Set(new TagValue(permit, 1, TagQuality.Good, ScadaTime.NowMs()));
        var granted = await Body(await op.PostAsJsonAsync("/api/control/check-text", new { tagId = tag, text = "LOT-9" }));
        Assert.Empty(granted.GetProperty("refusals").EnumerateArray());
    }

    [Fact]
    public async Task UnreleasedMomentaryLeaseIsReleasedOnRestart()
    {
        var admin = await _site.ClientAsync("admin");
        var tag = await TagAsync(admin, "点动恢复");
        await using (var db = await Runtime().CreateDbContextAsync())
        {
            db.MomentaryLeases.Add(new MomentaryLease
            {
                Id = Guid.NewGuid(), TagId = tag, ReleaseValue = 0, UserId = null, UserName = "opA", Source = "test",
                Kind = MomentaryLeaseKind.Press, State = MomentaryLeaseState.Active, ReleaseId = Guid.NewGuid(),
                CreatedTs = ScadaTime.NowMs(), StartDeadlineTs = ScadaTime.NowMs(), ExpiresTs = ScadaTime.NowMs() - 1000
            });
            await db.SaveChangesAsync();
        }
        await _site.RestartAsync();
        for (var i = 0; i < 100; i++)
        {
            await using var db = await Runtime().CreateDbContextAsync();
            if (!await db.MomentaryLeases.AnyAsync()) break;
            await Task.Delay(100);
        }
        await using (var db = await Runtime().CreateDbContextAsync())
            Assert.Empty(await db.MomentaryLeases.ToListAsync());
        Assert.Contains(Collector.Writes, x => x.TagId == tag && x.Value == 0);
    }

    [Fact]
    public async Task SequenceStartRequiresCheckAndChallengeIsSingleUse()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var tag = await TagAsync(admin, "顺控确认");
        var sequence = await Body(await admin.PostAsJsonAsync("/api/control/sequences", new
        {
            name = "确认顺控" + U(), areaId = _site.AreaA, dualApproval = false,
            steps = new[] { new { name = "写入", tagId = tag, value = 1.0, confirmExpression = (string?)null, timeoutMs = 1000, delayMs = 0 } }
        }));
        var id = sequence.GetProperty("id").GetInt32();
        var bare = await op.PostAsJsonAsync($"/api/control/sequences/{id}/start", new { confirmed = true });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bare.StatusCode);
        var grant = await Body(await op.PostAsJsonAsync($"/api/control/sequences/{id}/check-start", new { }));
        var challenge = grant.GetProperty("challenge").GetString();
        var started = await Body(await op.PostAsJsonAsync($"/api/control/sequences/{id}/start", new { confirmed = true, challenge }));
        Assert.False(started.GetProperty("pending").GetBoolean());
        var replay = await op.PostAsJsonAsync($"/api/control/sequences/{id}/start", new { confirmed = true, challenge });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task RecipeDownloadRequiresCheckChallengeBoundToUserVersionAndSingleUse()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var tag = await TagAsync(admin, "配方确认");
        var template = await Body(await admin.PostAsJsonAsync("/api/recipes/templates", new
        {
            name = "确认模板" + U(), areaId = _site.AreaA, description = (string?)null, dualApproval = false,
            parameters = new[] { new { key = "value", name = "值", tagId = tag, unit = (string?)null, min = 0.0, max = 10.0 } }
        }));
        var recipe = await Body(await admin.PostAsJsonAsync("/api/recipes", new
        {
            templateId = template.GetProperty("id").GetInt32(), name = "确认配方" + U(), description = (string?)null,
            values = new[] { new { key = "value", value = 1.0 } }, note = (string?)null
        }));
        var id = recipe.GetProperty("recipe").GetProperty("id").GetInt32();
        var bare = await op.PostAsJsonAsync($"/api/recipes/{id}/download", new { version = 0, confirmed = true });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bare.StatusCode);
        var grant = await Body(await op.PostAsJsonAsync($"/api/recipes/{id}/check", new { version = 0 }));
        var challenge = grant.GetProperty("confirmationChallenge").GetString();
        var otherUser = await admin.PostAsJsonAsync($"/api/recipes/{id}/download", new { version = 0, confirmed = true, challenge });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, otherUser.StatusCode);
        var accepted = await Body(await op.PostAsJsonAsync($"/api/recipes/{id}/download", new { version = 0, confirmed = true, challenge }));
        Assert.NotEqual("Rejected", accepted.GetProperty("status").GetString());
        var replay = await op.PostAsJsonAsync($"/api/recipes/{id}/download", new { version = 0, confirmed = true, challenge });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, replay.StatusCode);
        await _site.Services.GetRequiredService<Scada.Engine.Control.RecipeService>().WaitAsync(accepted.GetProperty("id").GetInt32());
    }

    private async Task<int> MomentaryTagAsync(HttpClient admin, string name)
    {
        var tag = await TagAsync(admin, name);
        await Body(await admin.PutAsJsonAsync($"/api/control/points/{tag}", new
        {
            requireConfirm = false, dualApproval = false, approvalTimeoutSec = 300, permitExpression = (string?)null, permitMessage = (string?)null,
            limitMin = (double?)null, limitMax = (double?)null, maxStep = (double?)null, feedbackExpression = (string?)null, feedbackTimeoutMs = 10000
        }));
        return tag;
    }

    [Fact]
    public async Task UnknownPressWritesReleaseAndClearsLease()
    {
        var admin = await _site.ClientAsync("admin");
        var tag = await MomentaryTagAsync(admin, "未知按下");
        Collector.Script(tag, GatewayCommandOutcome.Unknown, GatewayCommandOutcome.Confirmed);
        var momentary = _site.Services.GetRequiredService<Scada.Engine.Control.MomentaryControlService>();

        var result = await momentary.PressAsync(tag, 1, 0, 60_000, new(null, "momentary-test"), "test", Guid.NewGuid(), false, default);

        Assert.False(result.Result.Success);
        Assert.Equal(GatewayCommandOutcome.Unknown, result.Result.Outcome);
        Assert.Contains(Collector.Writes, x => x.TagId == tag && x.Value == 0);
        await using var db = await Runtime().CreateDbContextAsync();
        Assert.Empty(await db.MomentaryLeases.Where(x => x.TagId == tag).ToListAsync());
    }

    [Fact]
    public async Task FailedReleaseKeepsLeaseAndRetriesUntilSuccess()
    {
        var admin = await _site.ClientAsync("admin");
        var tag = await MomentaryTagAsync(admin, "释放重试");
        Collector.Script(tag, GatewayCommandOutcome.Unknown, GatewayCommandOutcome.Unknown, GatewayCommandOutcome.Confirmed);
        var momentary = _site.Services.GetRequiredService<Scada.Engine.Control.MomentaryControlService>();

        var result = await momentary.PressAsync(tag, 1, 0, 60_000, new(null, "momentary-test"), "test", Guid.NewGuid(), false, default);

        Assert.False(result.Result.Success);
        Assert.Contains("自动重试", result.Result.Message);
        await using (var db = await Runtime().CreateDbContextAsync())
            Assert.Single(await db.MomentaryLeases.Where(x => x.TagId == tag).ToListAsync());
        for (var i = 0; i < 50; i++)
        {
            await using var db = await Runtime().CreateDbContextAsync();
            if (!await db.MomentaryLeases.AnyAsync(x => x.TagId == tag)) break;
            await Task.Delay(100);
        }
        await using (var db = await Runtime().CreateDbContextAsync())
            Assert.Empty(await db.MomentaryLeases.Where(x => x.TagId == tag).ToListAsync());
        Assert.Contains(Collector.Writes, x => x.TagId == tag && x.Value == 0);
    }
}

public sealed class MomentaryRoleSite : ControlTests.ControlSite
{
    public sealed class MutableRole : INodeRole
    {
        public volatile bool Active = true;
        public bool IsActive => Active;
        public long Generation => Active ? 1 : 0;
    }

    public readonly MutableRole Role = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.RemoveAll<INodeRole>();
        services.AddSingleton<INodeRole>(Role);
    }
}

public sealed class MomentaryRoleTests : IClassFixture<MomentaryRoleSite>
{
    private readonly MomentaryRoleSite _site;
    public MomentaryRoleTests(MomentaryRoleSite site) => _site = site;
    private IDbContextFactory<RuntimeDbContext> Runtime() => _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();
    private ControlTests.FakeCollector Collector => _site.Services.GetRequiredService<ControlTests.FakeCollector>();

    [Fact]
    public async Task PromotionRecoversLeaseLeftWhileStandby()
    {
        var admin = await _site.ClientAsync("admin");
        var tag = (await (await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, "升主恢复" + Guid.NewGuid().ToString("N")[..5]))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        _site.Role.Active = false;
        await Task.Delay(300);
        await using (var db = await Runtime().CreateDbContextAsync())
        {
            db.MomentaryLeases.Add(new MomentaryLease
            {
                Id = Guid.NewGuid(), TagId = tag, ReleaseValue = 0, UserName = "standby", Source = "test",
                Kind = MomentaryLeaseKind.Press, State = MomentaryLeaseState.Active, ReleaseId = Guid.NewGuid(),
                CreatedTs = ScadaTime.NowMs(), StartDeadlineTs = ScadaTime.NowMs(), ExpiresTs = ScadaTime.NowMs() - 1000
            });
            await db.SaveChangesAsync();
        }
        Collector.Script(tag, GatewayCommandOutcome.Confirmed);
        _site.Role.Active = true;
        for (var i = 0; i < 50; i++)
        {
            await using var db = await Runtime().CreateDbContextAsync();
            if (!await db.MomentaryLeases.AnyAsync(x => x.TagId == tag)) break;
            await Task.Delay(100);
        }
        await using (var db = await Runtime().CreateDbContextAsync())
            Assert.Empty(await db.MomentaryLeases.Where(x => x.TagId == tag).ToListAsync());
        Assert.Contains(Collector.Writes, x => x.TagId == tag && x.Value == 0);
    }
}

public sealed class ControlConfirmationStoreTests
{
    [Fact]
    public void GrantIsSingleUse()
    {
        var store = new Scada.Engine.Control.ControlConfirmationStore();
        store.Grant(1, 10, 5, null);
        Assert.True(store.TryConsume(1, 10, 5, null, out _));
        Assert.False(store.TryConsume(1, 10, 5, null, out _));
    }

    [Fact]
    public void GrantIsBoundToUserTagValueAndText()
    {
        var store = new Scada.Engine.Control.ControlConfirmationStore();
        store.Grant(1, 10, 5, null);
        Assert.False(store.TryConsume(2, 10, 5, null, out _));
        Assert.False(store.TryConsume(1, 11, 5, null, out _));
        Assert.False(store.TryConsume(1, 10, 6, null, out _));
        Assert.True(store.TryConsume(1, 10, 5, null, out _));

        store.Grant(1, 10, 0, "A");
        Assert.False(store.TryConsume(1, 10, 0, "B", out _));
        Assert.True(store.TryConsume(1, 10, 0, "A", out _));
    }

    [Fact]
    public void ExpiredGrantIsRejected()
    {
        var store = new Scada.Engine.Control.ControlConfirmationStore { Lifetime = TimeSpan.FromMilliseconds(20) };
        store.Grant(1, 10, 5, null);
        Thread.Sleep(80);
        Assert.False(store.TryConsume(1, 10, 5, null, out _));
    }
}