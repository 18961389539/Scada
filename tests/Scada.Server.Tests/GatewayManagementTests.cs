using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Models;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public partial class GatewayCenterTests
{
    [Fact]
    public async Task ManagementIndexComparesCurrentConfigurationWithoutExposingCredentials()
    {
        await using var rig = await Rig.CreateAsync(); var initial = Assert.Single(await rig.Service.ListAsync(default));
        Assert.Equal(rig.Revision, initial.CurrentRevision); Assert.True(initial.ConfigurationConfirmed); Assert.Equal(1, initial.EnabledDeviceCount);
        Assert.Equal(rig.Clock.Now, initial.ServerTs); Assert.True(GatewayProtocol.IsRevision(initial.AdminRevision));
        await using (var db = rig.Factory.CreateDbContext())
        {
            var device = await db.Devices.SingleAsync(x => x.Id == rig.DeviceId); device.OpcPasswordProtected = "private-device-credential";
            (await db.Tags.SingleAsync(x => x.Id == rig.TagId)).Scale = 2; await db.SaveChangesAsync();
        }
        var changed = Assert.Single(await rig.Service.ListAsync(default)); Assert.NotEqual(initial.CurrentRevision, changed.CurrentRevision);
        Assert.False(changed.ConfigurationConfirmed); Assert.True(changed.Connected); Assert.Equal(initial.AdminRevision, changed.AdminRevision);
        var json = JsonSerializer.Serialize(changed, GatewayProtocol.Json); Assert.DoesNotContain("private-device-credential", json);
        Assert.DoesNotContain(rig.Caller.KeyStamp, json); Assert.DoesNotContain("sessionId", json); Assert.DoesNotContain("tokenHash", json);
        var configuration = await rig.Service.ConfigurationAsync(rig.Caller, rig.Session.SessionId, default); Assert.Equal(configuration.Revision, changed.CurrentRevision);
        await rig.Service.HeartbeatAsync(rig.Caller, rig.Session.SessionId, new(configuration.Revision, 0, 0, null, null, Capabilities: [GatewayProtocol.EngineeringCapability]), default);
        Assert.Contains(GatewayProtocol.EngineeringCapability, Assert.Single(await rig.Service.ListAsync(default)).Capabilities);
        rig.Clock.Now += 31000; Assert.False(Assert.Single(await rig.Service.ListAsync(default)).ConfigurationConfirmed);
    }
    [Fact]
    public async Task HeartbeatsDoNotInvalidateManagementEditsButANewSessionInvalidatesAReleaseConfirmation()
    {
        await using var rig = await Rig.CreateAsync(); var initial = Assert.Single(await rig.Service.ListAsync(default));
        rig.Clock.Now += 1000; await rig.Service.HeartbeatAsync(rig.Caller, rig.Session.SessionId, new(rig.Revision, 0, 0, null, null), default);
        Assert.Equal(initial.AdminRevision, Assert.Single(await rig.Service.ListAsync(default)).AdminRevision);
        rig.Clock.Now += 31000; var replacement = await rig.Service.HelloAsync(rig.Caller, new(1, Guid.NewGuid(), rig.Stream, "new-process"), default);
        var failure = await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.ChangeAsync(rig.Caller.Id, null, null, false, true, 1, "test", default, initial.AdminRevision));
        Assert.Equal(409, failure.StatusCode);
        await using var db = rig.Factory.CreateDbContext(); Assert.Equal(replacement.SessionId, (await db.Gateways.SingleAsync()).SessionId);
    }
    [Theory]
    [InlineData("rename")] [InlineData("disable")] [InlineData("rotate")] [InlineData("release")]
    public async Task StaleManagementWindowsCannotRepeatOrOverwriteAnotherAdministrativeChange(string change)
    {
        await using var rig = await Rig.CreateAsync(); var initial = Assert.Single(await rig.Service.ListAsync(default));
        await rig.Service.ChangeAsync(rig.Caller.Id, change == "rename" ? "changed" : null, change == "disable" ? false : null,
            change == "rotate", change == "release", 1, "test", default, initial.AdminRevision);
        var current = Assert.Single(await rig.Service.ListAsync(default)); Assert.NotEqual(initial.AdminRevision, current.AdminRevision);
        var failure = await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.ChangeAsync(rig.Caller.Id, "stale-overwrite", true, true, true, 1, "test", default, initial.AdminRevision));
        Assert.Equal("gateway_changed", failure.Code); Assert.Equal(current.AdminRevision, Assert.Single(await rig.Service.ListAsync(default)).AdminRevision);
    }
    [Fact]
    public async Task AssignmentAdviceUsesTheSameStoppedConfigurationProofAsTheWrite()
    {
        await using var rig = await Rig.CreateAsync(); var running = await rig.Service.AssignmentInfoAsync(rig.DeviceId, default); Assert.False(running.CanMove);
        await using (var db = rig.Factory.CreateDbContext()) { var device = await db.Devices.SingleAsync(); device.Enabled = false; device.DisabledSinceTs = rig.Clock.Now; await db.SaveChangesAsync(); }
        var waiting = await rig.Service.AssignmentInfoAsync(rig.DeviceId, default); Assert.False(waiting.CanMove); Assert.Contains("确认停用", waiting.BlockReason);
        var stopped = await rig.Service.ConfigurationAsync(rig.Caller, rig.Session.SessionId, default);
        await rig.Service.HeartbeatAsync(rig.Caller, rig.Session.SessionId, new(stopped.Revision, 0, 0, null, null), default);
        var ready = await rig.Service.AssignmentInfoAsync(rig.DeviceId, default); Assert.True(ready.CanMove); Assert.Equal(rig.Caller.Id, ready.GatewayId);
        await rig.Service.AssignAsync(rig.DeviceId, null, 1, "test", default, new(ready.GatewayId, ready.DisabledSinceTs));
        var moved = await rig.Service.AssignmentInfoAsync(rig.DeviceId, default); Assert.Null(moved.GatewayId); Assert.False(moved.Enabled);
    }
    [Theory]
    [InlineData("source")] [InlineData("disabled-time")]
    public async Task AssignmentRefusesAChangedSourceOrStopCycleEvenWhenTheTargetNowMatches(string change)
    {
        await using var rig = await Rig.CreateAsync();
        await using (var db = rig.Factory.CreateDbContext()) { var device = await db.Devices.SingleAsync(); device.Enabled = false; device.DisabledSinceTs = rig.Clock.Now; await db.SaveChangesAsync(); }
        var original = await rig.Service.AssignmentInfoAsync(rig.DeviceId, default);
        await using (var db = rig.Factory.CreateDbContext()) { var device = await db.Devices.SingleAsync(); if (change == "source") device.GatewayId = null; else device.DisabledSinceTs++; await db.SaveChangesAsync(); }
        var failure = await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.AssignAsync(rig.DeviceId, null, 1, "test", default, new(original.GatewayId, original.DisabledSinceTs)));
        Assert.Equal("assignment_changed", failure.Code);
    }
    [Fact]
    public async Task ManagedDeviceListIsScopedAndContainsOnlyOperationalDetails()
    {
        await using var rig = await Rig.CreateAsync(); await ReportDeviceAsync(rig, true);
        var device = Assert.Single(await rig.Service.ManagedDevicesAsync(rig.Caller.Id, default)); Assert.Equal(rig.DeviceId, device.Id);
        Assert.True(device.Online); Assert.Equal(1, device.TagCount);
        var another = await rig.Service.CreateAsync("another", 1, "test", default); Assert.Empty(await rig.Service.ManagedDevicesAsync(another.Id, default));
        var json = JsonSerializer.Serialize(device, GatewayProtocol.Json); Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("opcUserName", json);
    }
}
