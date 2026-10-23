using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Gateway.Tests;

public sealed class GatewayAgentStoreTests
{
    private const string Revision = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static TagValue Point(int id = 1, double value = 12.5) => new(id, value, TagQuality.Good, ScadaTime.NowMs(), 1234567);
    private static GatewayCommand Control(Guid? id = null, long? time = null) => new(id ?? Guid.NewGuid(), 1, 1, 20, Revision, time ?? ScadaTime.NowMs(), (time ?? ScadaTime.NowMs()) + 5000);

    [Fact]
    public async Task CommittedPacketsSurviveReopenAndOnlyMatchingOrderedAcknowledgementsDeleteThem()
    {
        using var temp = new StoreDirectory(); var store = temp.Open();
        var first = await store.AppendAsync(1, Revision, new[] { Point() }); var second = await store.AppendAsync(1, Revision, new[] { Point(value: 25) });
        var reopened = temp.Open(); Assert.Equal(store.StreamId, reopened.StreamId);
        var pending = (await reopened.PeekAsync())!; Assert.Equal(first, pending.Sequence); Assert.Equal(12.5, Assert.Single(pending.Packet.Values).Value);
        Assert.Equal(1234567, Assert.Single(pending.Packet.Values).SourceTs);
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.AcknowledgeAsync(new(Guid.NewGuid(), first)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AcknowledgeAsync(new(reopened.StreamId, second)));
        Assert.Equal(2, (await reopened.StatusAsync()).PendingBatches);
        await reopened.AcknowledgeAsync(new(reopened.StreamId, first)); await reopened.AcknowledgeAsync(new(reopened.StreamId, first));
        Assert.Equal(second, (await temp.Open().PeekAsync())!.Sequence);
        await reopened.AcknowledgeAsync(new(reopened.StreamId, second)); Assert.Null(await reopened.PeekAsync());
        var status = await reopened.StatusAsync(); Assert.Equal(0, status.PendingBytes); Assert.Equal(second, status.LastAcknowledged);
        Assert.Equal(second + 1, await temp.Open().AppendAsync(1, Revision, new[] { Point() }));
    }

    [Fact]
    public async Task FullSpoolNeverDropsOlderPacketsAndResumesAfterAcknowledgement()
    {
        using var temp = new StoreDirectory(); var store = temp.Open(1024); var ids = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            try { ids.Add(await store.AppendAsync(1, Revision, new[] { Point(value: i) })); }
            catch (GatewaySpoolFullException) { break; }
        }
        Assert.NotEmpty(ids); var full = await store.StatusAsync();
        await Assert.ThrowsAsync<GatewaySpoolFullException>(() => store.AppendAsync(1, Revision, new[] { Point() }));
        Assert.Equal(full, await store.StatusAsync()); Assert.Equal(ids[0], (await store.PeekAsync())!.Sequence);
        await store.AcknowledgeAsync(new(store.StreamId, ids[0]));
        Assert.Equal(ids[^1] + 1, await store.AppendAsync(1, Revision, new[] { Point() }));
    }

    [Fact]
    public async Task ConcurrentDeviceWritersCommitOneContinuousSequenceAcrossStoreInstances()
    {
        using var temp = new StoreDirectory(); var first = temp.Open(); var second = temp.Open();
        var ids = await Task.WhenAll(Enumerable.Range(1, 24).Select(id => Task.Run(() => (id % 2 == 0 ? first : second).AppendAsync(id, Revision, new[] { Point(id) }))));
        Assert.Equal(Enumerable.Range(1, 24).Select(x => (long)x), ids.Order());
        long bytes = 0;
        for (var i = 1; i <= 24; i++)
        {
            var next = (await first.PeekAsync())!; Assert.Equal(i, next.Sequence); bytes += Encoding.UTF8.GetByteCount(next.Json);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(next.Json))), next.Sha256);
            await second.AcknowledgeAsync(new(first.StreamId, next.Sequence));
        }
        Assert.True(bytes > 0); Assert.Equal(0, (await first.StatusAsync()).PendingBytes);
    }

    [Fact]
    public async Task InvalidOrCancelledBatchesCannotConsumeASequence()
    {
        using var temp = new StoreDirectory(); var store = temp.Open();
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(1, Revision, new[] { Point(value: double.NaN) }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(1, Revision, new[] { Point(), Point() }));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AppendAsync(1, Revision, new[] { Point() }, cancelled.Token));
        Assert.Equal(1, await store.AppendAsync(1, Revision, new[] { Point() }));
    }

    [Fact]
    public async Task CorruptedQueuedPayloadFailsClosedAndIsNeverAcknowledgedAutomatically()
    {
        using var temp = new StoreDirectory(); var store = temp.Open(); await store.AppendAsync(1, Revision, new[] { Point() });
        using (var db = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE Outbox SET Sha256='corrupt'"; command.ExecuteNonQuery(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PeekAsync()); Assert.Equal(1, (await store.StatusAsync()).PendingBatches);
    }

    [Fact]
    public async Task CommandReservationAndStartAreIdempotentAndChangedPayloadsAreRejected()
    {
        using var temp = new StoreDirectory(); var store = temp.Open(); var command = Control();
        Assert.Equal("Reserved", (await store.ReserveCommandAsync(command, command.CreatedTs)).State);
        Assert.True(await store.MarkCommandStartedAsync(command.Id, "protected-execution-token", command.CreatedTs));
        Assert.False(await store.MarkCommandStartedAsync(command.Id, "protected-execution-token", command.CreatedTs));
        Assert.Equal("Started", (await temp.Open().ReserveCommandAsync(command, command.CreatedTs)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReserveCommandAsync(command with { Value = 30 }, command.CreatedTs));
        var result = new GatewayCommandResult(GatewayCommandOutcome.Confirmed, "回读确认", 20);
        await store.CompleteCommandAsync(command.Id, result); await store.CompleteCommandAsync(command.Id, result);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteCommandAsync(command.Id, result with { Outcome = GatewayCommandOutcome.Failed }));
        var report = Assert.Single(await temp.Open().UnreportedResultsAsync()); Assert.Equal(result, report.Result); Assert.False(report.Reported);
        await store.AcknowledgeCommandResultAsync(command.Id); Assert.Empty(await temp.Open().UnreportedResultsAsync());
    }

    [Fact]
    public async Task ExpiredCommandsCannotStartAndRestartsNeverReplayStartedCommands()
    {
        using var temp = new StoreDirectory(); var store = temp.Open(); var started = Control(); var reserved = Control(); var expired = Control(time: ScadaTime.NowMs() - 10000);
        await store.ReserveCommandAsync(started, started.CreatedTs); await store.MarkCommandStartedAsync(started.Id, "protected-grant", started.CreatedTs);
        await store.ReserveCommandAsync(reserved, reserved.CreatedTs);
        Assert.Equal(GatewayCommandOutcome.NotSent, (await store.ReserveCommandAsync(expired, ScadaTime.NowMs())).Result!.Outcome);
        Assert.False(await store.MarkCommandStartedAsync(expired.Id, "grant", ScadaTime.NowMs()));
        var reopened = temp.Open(); using var lease = reopened.AcquireProcessLease(); Assert.Equal(2, await reopened.RecoverInterruptedCommandsAsync(lease));
        Assert.Equal(0, await reopened.RecoverInterruptedCommandsAsync(lease));
        Assert.False(await reopened.MarkCommandStartedAsync(started.Id, "old-grant", started.CreatedTs));
        var reports = await reopened.UnreportedResultsAsync();
        Assert.Equal(GatewayCommandOutcome.Unknown, Assert.Single(reports, x => x.Command.Id == started.Id).Result!.Outcome);
        Assert.Equal(GatewayCommandOutcome.NotSent, Assert.Single(reports, x => x.Command.Id == reserved.Id).Result!.Outcome);
    }

    [Fact]
    public async Task ProcessLeaseIsExclusiveAndRecoveryRejectsForeignOrReleasedLeases()
    {
        using var temp = new StoreDirectory(); var first = temp.Open(); var second = temp.Open();
        var lease = first.AcquireProcessLease(); Assert.Throws<IOException>(() => second.AcquireProcessLease());
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.RecoverInterruptedCommandsAsync(lease));
        lease.Dispose(); await Assert.ThrowsAsync<InvalidOperationException>(() => first.RecoverInterruptedCommandsAsync(lease));
        using var newLease = second.AcquireProcessLease(); Assert.Equal(0, await second.RecoverInterruptedCommandsAsync(newLease));
    }

    [Fact]
    public async Task CommandCapacityAndRetentionPreserveUnreportedOrStillValidResults()
    {
        using var temp = new StoreDirectory(); var store = temp.Open(maxCommands: 1); var command = Control(time: ScadaTime.NowMs() - 10 * 86_400_000L);
        await store.ReserveCommandAsync(command, ScadaTime.NowMs());
        await Assert.ThrowsAsync<GatewaySpoolFullException>(() => store.ReserveCommandAsync(Control(), ScadaTime.NowMs()));
        Assert.Equal(0, await store.PruneAcknowledgedCommandsAsync(ScadaTime.NowMs()));
        await store.AcknowledgeCommandResultAsync(command.Id); Assert.Equal(1, await store.PruneAcknowledgedCommandsAsync(ScadaTime.NowMs()));
        Assert.Equal(GatewayCommandOutcome.NotSent, (await store.ReserveCommandAsync(command, ScadaTime.NowMs())).Result!.Outcome);
    }

    [Fact]
    public async Task ProtectedConfigurationCanBeRecoveredWithPersistedKeysWithoutStoringPlaintext()
    {
        using var temp = new StoreDirectory(); var store = temp.Open(); var keys = new DirectoryInfo(System.IO.Path.Combine(temp.Root, "keys"));
        var protector = DataProtectionProvider.Create(keys).CreateProtector("Gateway.Config.v1");
        const string plaintext = "{\"opcPassword\":\"test-private-credential-1783\"}";
        await store.SaveProtectedConfigurationAsync(protector.Protect(plaintext));
        var reopened = temp.Open(); var cipher = (await reopened.LoadProtectedConfigurationAsync())!;
        Assert.Equal(plaintext, DataProtectionProvider.Create(keys).CreateProtector("Gateway.Config.v1").Unprotect(cipher));
        Assert.DoesNotContain("test-private-credential-1783", Encoding.UTF8.GetString(File.ReadAllBytes(temp.Path)));
    }

    [Fact]
    public void ExistingForeignOrNewerDatabasesAreRejectedWithoutChangingTheirContents()
    {
        using var temp = new StoreDirectory();
        using (var db = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE Existing(Value TEXT); INSERT INTO Existing VALUES ('keep')"; command.ExecuteNonQuery(); }
        var before = File.ReadAllBytes(temp.Path); Assert.Throws<InvalidDataException>(() => temp.Open()); Assert.Equal(before, File.ReadAllBytes(temp.Path));
    }

    [Theory]
    [InlineData(2)] [InlineData(-1)]
    public void UnsupportedGatewaySchemaIsNotOverwritten(int version)
    {
        using var temp = new StoreDirectory(); temp.Open();
        using (var db = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = $"PRAGMA user_version={version}"; command.ExecuteNonQuery(); }
        var before = File.ReadAllBytes(temp.Path); Assert.Throws<InvalidDataException>(() => temp.Open()); Assert.Equal(before, File.ReadAllBytes(temp.Path));
    }

    internal sealed class StoreDirectory : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scada-gateway-test-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "gateway.db");
        public GatewayAgentStore Open(long maxBytes = 1024 * 1024, int maxCommands = 100_000) => new(Path, maxBytes, maxCommands);
        public StoreDirectory() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var full = System.IO.Path.GetFullPath(Root); var parent = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (string.Equals(System.IO.Path.GetDirectoryName(full)?.TrimEnd(System.IO.Path.DirectorySeparatorChar), parent.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetFileName(full).StartsWith("scada-gateway-test-")) Directory.Delete(full, true);
        }
    }
}
