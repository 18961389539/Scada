using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class LocalCommandLedgerTests
{
    [Fact]
    public async Task NewRequestIdIsWrittenOnceAndReplayReturnsStoredResult()
    {
        await using var connection = TestRuntime.Open();
        var ledger = new LocalCommandLedger(TestRuntime.Factory(connection));
        var control = new FakeControl();
        var tag = new Tag { Id = 7, DeviceId = 3, Name = "valve" };
        var id = Guid.NewGuid();
        var ctx = new CollectorCommandContext(id, 1, "op");

        var first = await ledger.ExecuteAsync(control, 3, tag, 1, ctx, default);
        Assert.True(first.Success);
        Assert.Equal(GatewayCommandOutcome.Confirmed, first.Outcome);
        Assert.Equal(1, control.Writes);

        var replay = await ledger.ExecuteAsync(control, 3, tag, 1, ctx, default);
        Assert.True(replay.Success);
        Assert.Equal(GatewayCommandOutcome.Confirmed, replay.Outcome);
        Assert.Equal(1, control.Writes);
    }

    [Fact]
    public async Task RunningCommandWithoutResultIsNotRewrittenAndStartupMarksUnknown()
    {
        await using var connection = TestRuntime.Open();
        var factory = TestRuntime.Factory(connection);
        var id = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.LocalControlCommands.Add(new LocalControlCommand
            {
                Id = id, DeviceId = 3, TagId = 7, Value = 1, UserId = 1, UserName = "op",
                State = "Running", Attempt = 1, CreatedTs = ScadaTime.NowMs()
            });
            await db.SaveChangesAsync();
        }

        var ledger = new LocalCommandLedger(factory);
        var control = new FakeControl();
        var tag = new Tag { Id = 7, DeviceId = 3, Name = "valve" };
        var ctx = new CollectorCommandContext(id, 1, "op");

        var inflight = await ledger.ExecuteAsync(control, 3, tag, 1, ctx, default);
        Assert.False(inflight.Success);
        Assert.Equal(GatewayCommandOutcome.Unknown, inflight.Outcome);
        Assert.Contains("尚未结束", inflight.Message);
        Assert.Equal(0, control.Writes);

        Assert.Equal(1, await ledger.ReconcileStaleAsync(TimeSpan.Zero));
        var unknown = await ledger.ExecuteAsync(control, 3, tag, 1, ctx, default);
        Assert.False(unknown.Success);
        Assert.Equal(GatewayCommandOutcome.Unknown, unknown.Outcome);
        Assert.Contains("结果未知", unknown.Message);
        Assert.Contains("不会自动重发", unknown.Message);
        Assert.Equal(0, control.Writes);
    }

    [Fact]
    public async Task StaleRunningCommandIsMarkedUnknownOnClaimWithoutWriting()
    {
        await using var connection = TestRuntime.Open();
        var factory = TestRuntime.Factory(connection);
        var id = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.LocalControlCommands.Add(new LocalControlCommand
            {
                Id = id, DeviceId = 3, TagId = 7, Value = 1, UserId = 1, UserName = "op",
                State = "Running", Attempt = 1, CreatedTs = ScadaTime.NowMs() - LocalCommandLedger.StaleRunningMs - 1
            });
            await db.SaveChangesAsync();
        }

        var ledger = new LocalCommandLedger(factory);
        var control = new FakeControl();
        var result = await ledger.ExecuteAsync(control, 3, new Tag { Id = 7, DeviceId = 3, Name = "valve" }, 1,
            new CollectorCommandContext(id, 1, "op"), default);
        Assert.Equal(GatewayCommandOutcome.Unknown, result.Outcome);
        Assert.Contains("结果未知", result.Message);
        Assert.Equal(0, control.Writes);
    }

    private sealed class FakeControl : ICollectorControl
    {
        public int Writes;
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => [];
        public void RequestReload() { }
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.FromResult(new CommandExecutionResult(true, "ok", Outcome: GatewayCommandOutcome.Confirmed));
        }
    }
}
