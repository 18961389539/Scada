using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public partial class GatewayCenterTests
{
    private static async Task<int> EngineeringUserAsync(Rig rig, UserRole role = UserRole.Admin, bool advertise = true)
    {
        int userId;
        await using (var db = rig.Factory.CreateDbContext())
        { var user = new User { UserName = "engineer", Role = role, PasswordHash = "test-only" }; db.Users.Add(user); await db.SaveChangesAsync(); userId = user.Id; }
        await rig.Service.HelloAsync(rig.Caller, new(1, rig.Instance, rig.Stream, "engineering-test", advertise ? [GatewayProtocol.EngineeringCapability] : []), default);
        return userId;
    }
    [Fact]
    public async Task EngineeringReplyIsBoundToTheAssignedGatewayAndDuplicateRepliesNeverRecreateWork()
    {
        await using var rig = await Rig.CreateAsync(); var user = await EngineeringUserAsync(rig);
        var pending = rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Diagnostics, new { page = 1 }, user, default);
        var offer = await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default); var request = offer.Request!;
        Assert.Equal(user, request.UserId); Assert.Equal(rig.DeviceId, request.DeviceId);
        Assert.Equal(request.Id, (await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, request.Id, default)).Request!.Id);
        var other = await rig.Service.CreateAsync("unrelated-edge", 1, "test", default); var caller = new GatewayCaller(other.Id, GatewayCenterService.TokenHash(other.Token));
        var session = await rig.Service.HelloAsync(caller, new(1, Guid.NewGuid(), Guid.NewGuid(), "test"), default);
        Assert.Null((await rig.Service.EngineeringOfferAsync(caller, session.SessionId, null, default)).Request);
        var reply = new GatewayEngineeringReply(request.Id, 200, JsonSerializer.SerializeToElement(new { location = "gateway" }), null);
        await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.EngineeringReplyAsync(caller, session.SessionId, reply, default));
        await rig.Service.EngineeringReplyAsync(rig.Caller, rig.Session.SessionId, reply, default);
        Assert.Equal("gateway", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("location").GetString());
        await rig.Service.EngineeringReplyAsync(rig.Caller, rig.Session.SessionId, reply, default);
        Assert.Null((await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request);
    }
    [Fact]
    public async Task CancellingTheCallerTellsTheAgentToStopAndDoesNotQueueTheRequestForReconnect()
    {
        await using var rig = await Rig.CreateAsync(); var user = await EngineeringUserAsync(rig); using var cancel = new CancellationTokenSource();
        var pending = rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Diagnostics, new { }, user, cancel.Token);
        var request = (await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request!;
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(request.Id, (await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, request.Id, default)).CancelledRequest);
        Assert.Null((await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request);
    }
    [Theory]
    [InlineData("configuration")] [InlineData("user")]
    public async Task ChangedConfigurationOrPermissionsCannotReceiveAnOldEngineeringResult(string change)
    {
        await using var rig = await Rig.CreateAsync(); var user = await EngineeringUserAsync(rig);
        var pending = rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Diagnostics, new { }, user, default);
        var request = (await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request!;
        await using (var db = rig.Factory.CreateDbContext())
        {
            if (change == "configuration") (await db.Devices.SingleAsync(x => x.Id == rig.DeviceId)).Host = "changed";
            else (await db.Users.SingleAsync(x => x.Id == user)).Enabled = false;
            await db.SaveChangesAsync();
        }
        await rig.Service.EngineeringReplyAsync(rig.Caller, rig.Session.SessionId, new(request.Id, 200, JsonSerializer.SerializeToElement(new { sensitive = "must-not-return" }), null), default);
        var failure = await Assert.ThrowsAsync<GatewayRequestException>(() => pending); Assert.Equal(change == "configuration" ? 409 : 403, failure.StatusCode);
    }
    [Fact]
    public async Task OldSessionEngineeringRequestsCannotBeDeliveredToANewProcess()
    {
        await using var rig = await Rig.CreateAsync(); var user = await EngineeringUserAsync(rig);
        var pending = rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.OpcTrust, new { sha256 = new string('A', 64) }, user, default);
        var request = (await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request!;
        await rig.Service.ChangeAsync(rig.Caller.Id, null, null, false, true, 1, "test", default);
        var next = await rig.Service.HelloAsync(rig.Caller, new(1, Guid.NewGuid(), rig.Stream, "replacement", [GatewayProtocol.EngineeringCapability]), default);
        Assert.Equal(request.Id, (await rig.Service.EngineeringOfferAsync(rig.Caller, next.SessionId, request.Id, default)).CancelledRequest);
        await Assert.ThrowsAsync<GatewayRequestException>(() => pending);
        await using var db = rig.Factory.CreateDbContext(); var audit = await db.OperationLogs.SingleAsync(x => x.Action.Contains(request.Id.ToString("D")));
        Assert.Contains("核查", audit.Result); Assert.Equal(new string('A', 64), audit.NewValue);
    }
    [Fact]
    public async Task LegacyAgentsRejectUnsupportedEngineeringAndAnOperatorCanOnlyRequestProbes()
    {
        await using var rig = await Rig.CreateAsync(); var user = await EngineeringUserAsync(rig, UserRole.Operator, false);
        Assert.Equal(409, (await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Probe, new { }, user, default))).StatusCode);
        await rig.Service.HeartbeatAsync(rig.Caller, rig.Session.SessionId, new(rig.Revision, 0, 0, null, null, Capabilities: [GatewayProtocol.EngineeringCapability]), default);
        Assert.Equal(403, (await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Diagnostics, new { }, user, default))).StatusCode);
        var pending = rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Probe, new { }, user, default);
        var request = (await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request!;
        await rig.Service.EngineeringReplyAsync(rig.Caller, rig.Session.SessionId, new(request.Id, 200, JsonSerializer.SerializeToElement(new { connected = true }), null), default);
        Assert.True((await pending).GetProperty("connected").GetBoolean());
    }
    [Fact]
    public async Task EngineeringRequestsAreBoundedPerUserAndGateway()
    {
        await using var rig = await Rig.CreateAsync(); var user = await EngineeringUserAsync(rig); using var cancel = new CancellationTokenSource();
        var requests = Enumerable.Range(0, 4).Select(_ => rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Diagnostics, new { }, user, cancel.Token)).ToArray();
        try { Assert.Equal(429, (await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Diagnostics, new { }, user, default))).StatusCode); }
        finally { cancel.Cancel(); foreach (var request in requests) try { await request; } catch (OperationCanceledException) { } }
    }
    [Fact]
    public async Task CapturedCertificateScopeCannotFollowADeviceToADifferentGateway()
    {
        await using var rig = await Rig.CreateAsync(); var user = await EngineeringUserAsync(rig);
        var failure = await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.OpcTrust, new { sha256 = new string('A', 64) }, user, default, rig.Caller.Id + 1));
        Assert.Equal(409, failure.StatusCode); Assert.Null((await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request);
    }
    [Fact]
    public async Task ReplyAfterTheRequestDeadlineCannotBeReportedAsSuccess()
    {
        await using var rig = await Rig.CreateAsync(); var user = await EngineeringUserAsync(rig);
        var pending = rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.Diagnostics, new { }, user, default);
        var request = (await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request!;
        rig.Clock.Now = request.ExpiresTs;
        await rig.Service.EngineeringReplyAsync(rig.Caller, rig.Session.SessionId, new(request.Id, 200, JsonSerializer.SerializeToElement(new { late = true }), null), default);
        Assert.Equal(504, (await Assert.ThrowsAsync<GatewayRequestException>(() => pending)).StatusCode);
    }
    [Fact]
    public async Task RecreatedCenterDoesNotReplayAnOldEngineeringMutation()
    {
        await using var rig = await Rig.CreateAsync(); var user = await EngineeringUserAsync(rig); using var cancel = new CancellationTokenSource();
        var pending = rig.Service.EngineerAsync<JsonElement>(rig.DeviceId, GatewayEngineeringOperation.OpcTrust, new { sha256 = new string('A', 64) }, user, cancel.Token);
        var request = (await rig.Service.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request!;
        using var restarted = rig.MakeService(rig.History);
        Assert.Equal(request.Id, (await restarted.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, request.Id, default)).CancelledRequest);
        await restarted.EngineeringReplyAsync(rig.Caller, rig.Session.SessionId, new(request.Id, 200, JsonSerializer.SerializeToElement(new { changed = true }), null), default);
        Assert.Null((await restarted.EngineeringOfferAsync(rig.Caller, rig.Session.SessionId, null, default)).Request);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await using var db = rig.Factory.CreateDbContext(); Assert.Contains("结果未确认", (await db.OperationLogs.SingleAsync(x => x.Action.Contains(request.Id.ToString("D")))).Result);
    }
}
