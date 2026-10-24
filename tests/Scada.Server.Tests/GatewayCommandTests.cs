using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public partial class GatewayCenterTests
{
    [Fact]
    public async Task CommandIdentityAuthorizationAndReceiptsAreDurableAndIdempotent()
    {
        await using var rig = await Rig.CreateAsync(); await rig.EnableCommandsAsync(); var id = Guid.NewGuid();
        var actor = new CollectorCommandContext(id, rig.OperatorId, "operator");
        var first = rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 42, actor, default);
        var repeated = rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 42, actor, default);
        var offered = await rig.Service.CommandOffersAsync(rig.Caller, rig.Session.SessionId, default); Assert.Equal(id, Assert.Single(offered.Commands).Id);
        var grant = await rig.Service.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, id, default);
        var again = await rig.Service.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, id, default); Assert.Equal(grant.ExecutionToken, again.ExecutionToken);
        var rebuilt = rig.MakeService(rig.History);
        await rebuilt.HeartbeatAsync(rig.Caller, rig.Session.SessionId, new(rig.Revision, 0, 0, null, null, [new(rig.DeviceId, "remote", true, null, rig.Clock.Now, 1, 1, 0, 0)]), default);
        Assert.Equal(grant.ExecutionToken, (await rebuilt.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, id, default)).ExecutionToken);
        var receipt = new GatewayCommandReceipt(id, rig.Stream, grant.ExecutionToken, new(GatewayCommandOutcome.Confirmed, "写入及回读成功", 42));
        await rebuilt.ReceiveCommandResultAsync(rig.Caller, rig.Session.SessionId, receipt, default);
        await rebuilt.ReceiveCommandResultAsync(rig.Caller, rig.Session.SessionId, receipt, default);
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Success); Assert.Equal(id, (await repeated).CommandId);
        Assert.False((await rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 43, actor, default)).Success);
        Assert.Null(await rebuilt.CommandStatusAsync(id, rig.OperatorId + 1000, false, default));
        var status = await rebuilt.CommandStatusAsync(id, rig.OperatorId, false, default); Assert.Equal(GatewayCommandOutcome.Confirmed, status!.Result!.Outcome);
        await Assert.ThrowsAsync<GatewayRequestException>(() => rebuilt.ReceiveCommandResultAsync(rig.Caller, rig.Session.SessionId, receipt with { Result = new(GatewayCommandOutcome.Unknown, "different") }, default));
        await using var db = rig.Factory.CreateDbContext(); Assert.Equal(1, await db.GatewayCommands.CountAsync());
        Assert.Equal(1, await db.OperationLogs.CountAsync(x => x.Action.StartsWith("远程命令回执"))); rebuilt.Dispose();
    }
    [Fact]
    public async Task AuthorizedTimeoutIsUnknownAndLateVerifiedReceiptRefinesItWithoutReexecution()
    {
        await using var rig = await Rig.CreateAsync(); await rig.EnableCommandsAsync(); var id = Guid.NewGuid();
        var pending = rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 7, new(id, rig.OperatorId, "operator"), default);
        await rig.Service.CommandOffersAsync(rig.Caller, rig.Session.SessionId, default);
        var grant = await rig.Service.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, id, default);
        rig.Clock.Now += 11000;
        Assert.Equal(GatewayCommandOutcome.Unknown, (await rig.Service.CommandStatusAsync(id, rig.OperatorId, false, default))!.Result!.Outcome);
        Assert.Equal(GatewayCommandOutcome.Unknown, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, id, default));
        await rig.Service.ReceiveCommandResultAsync(rig.Caller, rig.Session.SessionId, new(id, rig.Stream, grant.ExecutionToken, new(GatewayCommandOutcome.Confirmed, "迟到的持久执行结果", 7)), default);
        Assert.Equal(GatewayCommandOutcome.Confirmed, (await rig.Service.CommandStatusAsync(id, rig.OperatorId, false, default))!.Result!.Outcome);
        Assert.Empty((await rig.Service.CommandOffersAsync(rig.Caller, rig.Session.SessionId, default)).Commands);
    }
    [Theory]
    [InlineData("expired")] [InlineData("user")] [InlineData("configuration")] [InlineData("session")]
    public async Task AReservedCommandCannotExecuteAfterItsConditionsChange(string change)
    {
        await using var rig = await Rig.CreateAsync(); await rig.EnableCommandsAsync(); var id = Guid.NewGuid();
        var pending = rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 7, new(id, rig.OperatorId, "operator"), default);
        await rig.Service.CommandOffersAsync(rig.Caller, rig.Session.SessionId, default);
        if (change == "expired") rig.Clock.Now += 11000;
        if (change == "session") await rig.Service.ChangeAsync(rig.Caller.Id, null, null, false, true, 1, "test", default);
        if (change is "user" or "configuration")
        {
            await using var db = rig.Factory.CreateDbContext();
            if (change == "user") (await db.Users.SingleAsync(x => x.Id == rig.OperatorId)).Enabled = false;
            else (await db.Tags.SingleAsync(x => x.Id == rig.TagId)).Scale = 2;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, id, default));
        Assert.Equal(GatewayCommandOutcome.NotSent, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CallerCancellationDistinguishesUnsentAndPossiblyStartedCommands(bool authorized)
    {
        await using var rig = await Rig.CreateAsync(); await rig.EnableCommandsAsync(); var id = Guid.NewGuid(); using var cancel = new CancellationTokenSource();
        var pending = rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 7, new(id, rig.OperatorId, "operator"), cancel.Token);
        await rig.Service.CommandOffersAsync(rig.Caller, rig.Session.SessionId, default);
        if (authorized) await rig.Service.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, id, default);
        cancel.Cancel(); var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(authorized ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent, result.Outcome);
        Assert.Empty((await rig.Service.CommandOffersAsync(rig.Caller, rig.Session.SessionId, default)).Commands);
    }
    [Fact]
    public async Task ANewProcessCanReconcileOldResultsButCannotAuthorizeOldSessionCommands()
    {
        await using var rig = await Rig.CreateAsync(); await rig.EnableCommandsAsync(); var id = Guid.NewGuid();
        var pending = rig.Service.ExecuteCommandAsync(rig.DeviceId, rig.TagId, 7, new(id, rig.OperatorId, "operator"), default);
        await rig.Service.CommandOffersAsync(rig.Caller, rig.Session.SessionId, default);
        var grant = await rig.Service.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, id, default);
        await rig.Service.ChangeAsync(rig.Caller.Id, null, null, false, true, 1, "test", default);
        var next = await rig.Service.HelloAsync(rig.Caller, new(1, Guid.NewGuid(), rig.Stream, "new-process"), default);
        Assert.Equal(GatewayCommandOutcome.Unknown, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.AuthorizeCommandAsync(rig.Caller, rig.Session.SessionId, id, default));
        var receipt = new GatewayCommandReceipt(id, rig.Stream, grant.ExecutionToken, new(GatewayCommandOutcome.Unknown, "网关重启，执行结果未知"));
        await Assert.ThrowsAsync<GatewayRequestException>(() => rig.Service.ReceiveCommandResultAsync(rig.Caller, next.SessionId, receipt with { StreamId = Guid.NewGuid() }, default));
        await rig.Service.ReceiveCommandResultAsync(rig.Caller, next.SessionId, receipt, default);
        Assert.Equal("网关重启，执行结果未知", (await rig.Service.CommandStatusAsync(id, rig.OperatorId, false, default))!.Result!.Message);
    }
}
