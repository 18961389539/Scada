using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.Alarms;
using Scada.Server.Services;
using Scada.Server.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;

namespace Scada.Server.Tests;

public partial class GatewayCenterTests
{
    private static Task ReportDeviceAsync(Rig rig, bool online, long? sourceTs = null, long? pollTs = null, GatewaySession? session = null) =>
        rig.Service.HeartbeatAsync(rig.Caller, (session ?? rig.Session).SessionId, new(rig.Revision, 0, 0, null, null,
            [new(rig.DeviceId, "remote", online, online ? null : "connection failed", pollTs ?? rig.Clock.Now, 1, 1, 0, 0, sourceTs ?? rig.Clock.Now)]), default);
    private static async Task<AlarmEvent[]> DeviceAlarmsAsync(Rig rig)
    { await using var db = rig.Factory.CreateDbContext(); return await db.AlarmEvents.AsNoTracking().Where(x => x.DeviceId == rig.DeviceId && x.GatewayId != null).OrderBy(x => x.Id).ToArrayAsync(); }
    private static async Task<AlarmEvent> RaiseRemoteOfflineAsync(Rig rig)
    {
        await ReportDeviceAsync(rig, false); Assert.Equal(0, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        rig.Clock.Now += 3000; await ReportDeviceAsync(rig, false);
        Assert.Equal(1, await rig.Service.ReconcileDeviceAlarmsAsync(default)); return Assert.Single(await DeviceAlarmsAsync(rig), x => x.EndTs is null);
    }
    [Fact]
    public async Task RemoteOfflineAlarmDebouncesDeduplicatesKeepsAcknowledgementAndRecoversOnFreshEvidence()
    {
        await using var rig = await Rig.CreateAsync(); var alarm = await RaiseRemoteOfflineAsync(rig);
        Assert.Null(alarm.EndTs); Assert.Equal(rig.Caller.Id, alarm.GatewayId);
        Assert.Equal(0, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        Assert.True(await new AlarmService(rig.Factory, rig.Notifications).AckAsync(alarm.Id, "operator"));
        rig.Clock.Now++; await ReportDeviceAsync(rig, true);
        Assert.Equal(1, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        var recovered = Assert.Single(await DeviceAlarmsAsync(rig)); Assert.Equal(alarm.Id, recovered.Id);
        Assert.NotNull(recovered.EndTs); Assert.Contains("通信恢复", recovered.EndReason); Assert.Equal("operator", recovered.AckUser);
        Assert.Equal(1, rig.Notifications.Events.Count(x => x.Id == alarm.Id && x.Action == "raise"));
        Assert.Equal(1, rig.Notifications.Events.Count(x => x.Id == alarm.Id && x.Action == "recover"));
        rig.Clock.Now++; var next = await RaiseRemoteOfflineAsync(rig); Assert.NotEqual(alarm.Id, next.Id); Assert.Equal(2, (await DeviceAlarmsAsync(rig)).Length);
    }
    [Fact]
    public async Task LeaseExpiryCreatesOneAlarmAndOldSessionBackfillCannotRecoverIt()
    {
        await using var rig = await Rig.CreateAsync(); await ReportDeviceAsync(rig, true);
        await rig.Service.ReconcileDeviceAlarmsAsync(default); await rig.AcceptAsync(rig.Packet(1, 42));
        rig.Clock.Now += 33000; Assert.Equal(1, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        var alarm = Assert.Single(await DeviceAlarmsAsync(rig)); Assert.Contains("租约", alarm.Message);
        var next = await rig.Service.HelloAsync(rig.Caller, new(1, Guid.NewGuid(), rig.Stream, "restarted"), default);
        await rig.Service.HeartbeatAsync(rig.Caller, next.SessionId, new(rig.Revision, 0, 0, null, null), default);
        Assert.True(await rig.Service.ProcessOneAsync(default)); Assert.Equal(0, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        Assert.Null(Assert.Single(await DeviceAlarmsAsync(rig)).EndTs);
        rig.Clock.Now++; await ReportDeviceAsync(rig, true, session: next); await rig.Service.ReconcileDeviceAlarmsAsync(default);
        Assert.NotNull(Assert.Single(await DeviceAlarmsAsync(rig)).EndTs);
    }
    [Fact]
    public async Task CenterRecreationPreservesTheActiveEventAndPendingOfflineDelay()
    {
        await using var rig = await Rig.CreateAsync(); await ReportDeviceAsync(rig, false); await rig.Service.ReconcileDeviceAlarmsAsync(default);
        rig.Clock.Now += 3000;
        using (var restarted = rig.MakeService(rig.History)) Assert.Equal(1, await restarted.ReconcileDeviceAlarmsAsync(default));
        var alarm = Assert.Single(await DeviceAlarmsAsync(rig));
        using (var restarted = rig.MakeService(rig.History)) Assert.Equal(0, await restarted.ReconcileDeviceAlarmsAsync(default));
        Assert.Equal(alarm.Id, Assert.Single(await DeviceAlarmsAsync(rig)).Id); Assert.Null(Assert.Single(await DeviceAlarmsAsync(rig)).EndTs);
    }
    [Fact]
    public async Task MissingReportsGetAConfigurationGraceButDoNotStayHealthyForever()
    {
        await using var rig = await Rig.CreateAsync(); Assert.Equal(0, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        rig.Clock.Now += 14999; Assert.Equal(0, await rig.Service.ReconcileDeviceAlarmsAsync(default)); Assert.Empty(await DeviceAlarmsAsync(rig));
        rig.Clock.Now++; await rig.Service.ReconcileDeviceAlarmsAsync(default);
        rig.Clock.Now += 3000; Assert.Equal(1, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        Assert.Contains("新鲜设备状态", Assert.Single(await DeviceAlarmsAsync(rig)).Message);
    }
    [Theory]
    [InlineData("stale")] [InlineData("future")]
    public async Task StaleOrFutureHeartbeatEvidenceCannotRecoverAnOfflineAlarm(string fault)
    {
        await using var rig = await Rig.CreateAsync(); await RaiseRemoteOfflineAsync(rig); rig.Clock.Now++;
        await ReportDeviceAsync(rig, true, sourceTs: fault == "future" ? rig.Clock.Now + 10000 : rig.Clock.Now, pollTs: fault == "stale" ? rig.Clock.Now - 20000 : rig.Clock.Now);
        await rig.Service.ReconcileDeviceAlarmsAsync(default); Assert.Null(Assert.Single(await DeviceAlarmsAsync(rig)).EndTs);
        rig.Clock.Now++; await ReportDeviceAsync(rig, true); await rig.Service.ReconcileDeviceAlarmsAsync(default);
        Assert.NotNull(Assert.Single(await DeviceAlarmsAsync(rig)).EndTs);
    }
    [Fact]
    public async Task OlderGoodTelemetryCannotOverwriteANewerCommunicationFailure()
    {
        await using var rig = await Rig.CreateAsync(); await RaiseRemoteOfflineAsync(rig);
        await rig.AcceptAsync(rig.Packet(1, 42, rig.Clock.Now - 1)); await rig.Service.ProcessOneAsync(default);
        await rig.Service.ReconcileDeviceAlarmsAsync(default); Assert.Null(Assert.Single(await DeviceAlarmsAsync(rig)).EndTs);
        rig.Clock.Now++; var packet = rig.Packet(2, 42) with { Values = [new(rig.TagId, 42, TagQuality.Bad, rig.Clock.Now, Error: "bad node quality")] };
        await rig.AcceptAsync(packet); await rig.Service.ProcessOneAsync(default); await rig.Service.ReconcileDeviceAlarmsAsync(default);
        Assert.NotNull(Assert.Single(await DeviceAlarmsAsync(rig)).EndTs); // A bad variable is not a lost device connection.
    }
    [Fact]
    public async Task PreviousConfigurationReportsCannotRecoverTheCurrentOfflineEpisode()
    {
        await using var rig = await Rig.CreateAsync(); await RaiseRemoteOfflineAsync(rig);
        await using (var db = rig.Factory.CreateDbContext()) { (await db.Devices.SingleAsync(x => x.Id == rig.DeviceId)).Host = "changed"; await db.SaveChangesAsync(); }
        await rig.Config.ReloadAsync(); rig.Clock.Now++; await ReportDeviceAsync(rig, true); await rig.Service.ReconcileDeviceAlarmsAsync(default);
        Assert.Null(Assert.Single(await DeviceAlarmsAsync(rig)).EndTs);
        var current = await rig.Service.ConfigurationAsync(rig.Caller, rig.Session.SessionId, default);
        await rig.Service.HeartbeatAsync(rig.Caller, rig.Session.SessionId, new(current.Revision, 0, 0, null, null,
            [new(rig.DeviceId, "remote", true, null, rig.Clock.Now, 1, 1, 0, 0, rig.Clock.Now)]), default);
        await rig.Service.ReconcileDeviceAlarmsAsync(default); Assert.NotNull(Assert.Single(await DeviceAlarmsAsync(rig)).EndTs);
    }
    [Theory]
    [InlineData("disabled", "停用")] [InlineData("deleted", "删除")] [InlineData("moved", "变更")] [InlineData("empty", "无采集测点")]
    public async Task AdministrativeChangesCloseTheOldEpisodeWithoutClaimingCommunicationRecovery(string change, string reason)
    {
        await using var rig = await Rig.CreateAsync(); var alarm = await RaiseRemoteOfflineAsync(rig);
        await using (var db = rig.Factory.CreateDbContext())
        {
            var device = await db.Devices.SingleAsync(x => x.Id == rig.DeviceId);
            if (change == "disabled") device.Enabled = false;
            else if (change == "deleted") db.Devices.Remove(device);
            else if (change == "moved") device.GatewayId = null;
            else db.Tags.RemoveRange(await db.Tags.Where(x => x.DeviceId == rig.DeviceId).ToListAsync());
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await rig.Service.ReconcileDeviceAlarmsAsync(default));
        var closed = Assert.Single(await DeviceAlarmsAsync(rig)); Assert.Equal(alarm.Id, closed.Id); Assert.Contains(reason, closed.EndReason);
        Assert.DoesNotContain(rig.Notifications.Events, x => x.Id == alarm.Id && x.Action == "recover");
        Assert.Contains(rig.Notifications.Events, x => x.Id == alarm.Id && x.Action == "close");
        await using var result = rig.Factory.CreateDbContext(); Assert.Empty(await result.GatewayDeviceHealth.ToListAsync());
    }
    [Fact]
    public async Task NotificationFailureDoesNotRollBackOrDuplicateAnAlarm()
    {
        await using var rig = await Rig.CreateAsync(); rig.Notifications.Fail = true; await RaiseRemoteOfflineAsync(rig);
        Assert.Equal(0, await rig.Service.ReconcileDeviceAlarmsAsync(default)); Assert.Single(await DeviceAlarmsAsync(rig));
    }
    [Fact]
    public async Task MovingALocalDeviceToAGatewayClosesThePreviousLocalOfflineEpisodeWithAReason()
    {
        await using var rig = await Rig.CreateAsync();
        await using (var db = rig.Factory.CreateDbContext()) { db.AlarmEvents.Add(new() { DeviceId = rig.DeviceId, StartTs = rig.Clock.Now, Message = "previous local offline" }); await db.SaveChangesAsync(); }
        await rig.Service.ReconcileDeviceAlarmsAsync(default);
        await using var result = rig.Factory.CreateDbContext(); var alarm = await result.AlarmEvents.SingleAsync();
        Assert.NotNull(alarm.EndTs); Assert.Contains("采集位置", alarm.EndReason); Assert.Contains(rig.Notifications.Events, x => x.Id == alarm.Id && x.Action == "close");
    }
    [Fact]
    public async Task AnUnrelatedDeviceStaysHealthyUntilTheSharedGatewayLeaseExpires()
    {
        await using var rig = await Rig.CreateAsync(); int otherId;
        await using (var db = rig.Factory.CreateDbContext())
        {
            var other = new Device { Name = "other", GatewayId = rig.Caller.Id }; db.Devices.Add(other); await db.SaveChangesAsync(); otherId = other.Id;
            db.Tags.Add(new() { DeviceId = otherId, Name = "value" }); await db.SaveChangesAsync();
        }
        await rig.Config.ReloadAsync(); var config = await rig.Service.ConfigurationAsync(rig.Caller, rig.Session.SessionId, default);
        async Task Report() => await rig.Service.HeartbeatAsync(rig.Caller, rig.Session.SessionId, new(config.Revision, 0, 0, null, null,
            [new(rig.DeviceId, "remote", false, "failure", rig.Clock.Now, 1, 1, 0, 0, rig.Clock.Now), new(otherId, "other", true, null, rig.Clock.Now, 1, 1, 0, 0, rig.Clock.Now)]), default);
        await Report(); await rig.Service.ReconcileDeviceAlarmsAsync(default); rig.Clock.Now += 3000; await Report(); await rig.Service.ReconcileDeviceAlarmsAsync(default);
        await using (var db = rig.Factory.CreateDbContext()) Assert.Equal(rig.DeviceId, (await db.AlarmEvents.SingleAsync()).DeviceId);
        rig.Clock.Now += 33000; await rig.Service.ReconcileDeviceAlarmsAsync(default);
        await using (var db = rig.Factory.CreateDbContext()) Assert.Equal(2, await db.AlarmEvents.CountAsync(x => x.EndTs == null));
    }
    [Fact]
    public async Task ReleasingASessionCannotReuseItsHealthyStatusToAuthorizeANewCommand()
    {
        await using var rig = await Rig.CreateAsync(); await rig.EnableCommandsAsync();
        await rig.Service.ChangeAsync(rig.Caller.Id, null, null, false, true, 1, "test", default);
        var next = await rig.Service.HelloAsync(rig.Caller, new(1, Guid.NewGuid(), rig.Stream, "replacement"), default);
        await rig.Service.HeartbeatAsync(rig.Caller, next.SessionId, new(rig.Revision, 0, 0, null, null), default);
        var result = await rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 7, new(Guid.NewGuid(), rig.OperatorId, "operator"), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GatewayCommandOutcome.NotSent, result.Outcome); Assert.Empty((await rig.Service.CommandOffersAsync(rig.Caller, next.SessionId, default)).Commands);
    }
    [Fact]
    public async Task ActiveAlarmEndpointKeepsEpisodesOlderThanOneDayAndHonorsAnExplicitRange()
    {
        await using var rig = await Rig.CreateAsync(); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using (var db = rig.Factory.CreateDbContext())
        { db.AlarmEvents.Add(new() { DeviceId = rig.DeviceId, GatewayId = rig.Caller.Id, StartTs = now - 3 * 86400000L, Message = "ongoing for three days" }); await db.SaveChangesAsync(); }
        var builder = WebApplication.CreateBuilder(); builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(x => x.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAuthorization(); builder.Services.AddSingleton(new AlarmService(rig.Factory, rig.Notifications));
        // 报警列表按区域过滤：这里以全局管理员身份访问。
        builder.Services.AddSingleton(new Scada.Data.AccessModelProvider(_ => Task.FromResult(Scada.Core.Security.AccessModel.Build([], [], [], [(1, UserRole.Admin, true)], [], Scada.Core.Models.ScadaTime.NowMs()))));
        await using var app = builder.Build();
        app.Use((context, next) => { context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier, "1")], "test")); return next(context); });
        app.MapAlarmEndpoints(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Single((await client.GetFromJsonAsync<AlarmEvent[]>("/api/alarms?activeOnly=true"))!);
        Assert.Empty((await client.GetFromJsonAsync<AlarmEvent[]>($"/api/alarms?activeOnly=true&from={now - 86400000L}"))!);
        Assert.Empty((await client.GetFromJsonAsync<AlarmEvent[]>("/api/alarms"))!); // The default history window remains one day.
        await app.StopAsync();
    }
}
