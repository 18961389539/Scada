using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using HistoryValue = Scada.Core.Models.HistoryValue;
using Scada.Engine.Maintenance;

namespace Scada.Engine.Tests;

public sealed class OperationLogRetentionTests
{
    [Fact]
    public async Task RunOnceDeletesLogsOlderThanRetentionAndSkipsWhenDisabled()
    {
        await using var connection = TestRuntime.Open();
        var runtimeFactory = TestRuntime.Factory(connection);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using (var db = runtimeFactory.CreateDbContext())
        {
            db.OperationLogs.AddRange(
                new OperationLog { Ts = now - 400L * 86_400_000L, UserName = "old", Action = "expired", Result = "ok" },
                new OperationLog { Ts = now - 10L * 86_400_000L, UserName = "new", Action = "kept", Result = "ok" });
            await db.SaveChangesAsync();
            await db.SaveChangesAsync();
        }

        var settings = new ScadaSettings
        {
            OperationLogRetentionDays = 365,
            FullBackupIntervalHours = 0,
            ConfigDbPath = Path.Combine(Path.GetTempPath(), "missing-config.db"),
            BackupRoot = Path.Combine(Path.GetTempPath(), "scada-retention-" + Guid.NewGuid().ToString("N")),
        };
        var service = new MaintenanceService(Options.Create(settings), new NoHistory(), NullLogger<MaintenanceService>.Instance, runtimeFactory);
        await service.RunOnceAsync(default);
        await using (var db = runtimeFactory.CreateDbContext())
        {
            var rows = await db.OperationLogs.AsNoTracking().ToListAsync();
            Assert.Single(rows);
            Assert.Equal("kept", rows[0].Action);
        }

        settings.OperationLogRetentionDays = 0;
        await using (var db = runtimeFactory.CreateDbContext())
        {
            db.OperationLogs.Add(new OperationLog { Ts = now - 800L * 86_400_000L, UserName = "older", Action = "also-kept", Result = "ok" });
            await db.SaveChangesAsync();
            await db.SaveChangesAsync();
        }
        await service.RunOnceAsync(default);
        await using (var db = runtimeFactory.CreateDbContext())
            Assert.Equal(2, await db.OperationLogs.CountAsync());
    }

    private sealed class NoHistory : IHistoryBackend
    {
        public Task CleanupRetentionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public HistoryStoreUsage MeasureUsage() => new(0, 0, 0, 0, 0, 0, true);
        public Task<IReadOnlyList<string>> PackBackupAsync(string stagingHistoryDirectory, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public IReadOnlyList<HistoryReplicaCursor> GetReplicaCursors() => [];
        public HistoryReplicaRows ReadReplica(string file, string table, long after, int limit) => new(file, table, []);
        public void ApplyReplica(HistoryReplicaRows batch) { }
        public Task<IReadOnlyList<HistoryPendingWindow>> ListPendingWindowsAsync(bool raw, long latestStartInclusive, int batchSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HistoryPendingWindow>>([]);
        public Task AcknowledgePendingWindowAsync(HistoryPendingWindow window, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> HasPendingRawWindowsAsync(long fromInclusive, long toExclusive, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task WriteRawAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<HistorySeries>> QueryRawAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HistorySeries>>([]);
        public Task WriteAggregate5MinAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WriteAggregateHourAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<HistorySeries>> QueryAggregate5MinAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HistorySeries>>([]);
        public Task<IReadOnlyList<HistorySeries>> QueryAggregateHourAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HistorySeries>>([]);
        public Task<IReadOnlyList<AggregateValue>> QueryRawAggregatesAsync(long from, long to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AggregateValue>>([]);
        public Task<IReadOnlyList<AggregateValue>> Query5MinAggregatesAsync(long from, long to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AggregateValue>>([]);
        public Task<IReadOnlyList<AggregatePoint>> QueryHourAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AggregatePoint>>([]);
        public Task<IReadOnlyList<AggregatePoint>> QueryAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AggregatePoint>>([]);
        public Task WriteGatewayHistoryAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<HistoryValue> ReadAsync(IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
