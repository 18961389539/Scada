using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Engine.Maintenance;

namespace Scada.Engine.Tests;

public class SpaceReclaimTests
{
    [Fact]
    public async Task VacuumSwitchesLegacyNoneModeAndReclaimsFreelist()
    {
        using var dir = new TempDir("scada-reclaim-");
        var path = Path.Combine(dir.Path, "legacy.db");
        CreateLegacyDatabase(path, rows: 80, thenDelete: true);
        var before = SpaceReclaimService.Probe("runtime", path);
        Assert.Equal("none", before.AutoVacuumMode);
        Assert.True(before.FreelistPages > 0, "删除行后应留下空闲页");

        var service = NewService(dir.Path, path);
        var run = await service.ReclaimOneAsync(before, allowVacuum: true, CancellationToken.None);
        Assert.True(run.Ok, run.Reason);
        Assert.Equal(SpaceReclaimService.ActionVacuum, run.Action);

        var after = SpaceReclaimService.Probe("runtime", path);
        Assert.Equal("incremental", after.AutoVacuumMode);
        Assert.True(after.FileBytes < before.FileBytes, $"VACUUM 后文件应缩小：{before.FileBytes} → {after.FileBytes}");
    }

    [Fact]
    public async Task ExclusiveVacuumIsSkippedWhenHistoryQueriesAreBusy()
    {
        using var dir = new TempDir("scada-reclaim-busy-");
        var path = Path.Combine(dir.Path, "legacy.db");
        CreateLegacyDatabase(path, rows: 40, thenDelete: true);
        var target = SpaceReclaimService.Probe("config", path);
        var service = NewService(dir.Path, path, activeQueries: () => 2);
        var run = await service.ReclaimOneAsync(target, allowVacuum: true, CancellationToken.None);
        Assert.True(run.Ok);
        Assert.Equal(SpaceReclaimService.ActionSkipped, run.Action);
        Assert.Contains("历史查询正忙", run.Reason);
        Assert.Equal("none", SpaceReclaimService.Probe("config", path).AutoVacuumMode);
    }

    private static SpaceReclaimService NewService(string root, string dbPath, Func<int>? activeQueries = null)
    {
        var settings = new ScadaSettings
        {
            ConfigDbPath = dbPath,
            RuntimeDbPath = Path.Combine(root, "missing-runtime.db"),
            HistoryRoot = Path.Combine(root, "history"),
            SpaceReclaimVacuumTimeoutSeconds = 30
        };
        return new SpaceReclaimService(Options.Create(settings), NullLogger<SpaceReclaimService>.Instance, historyFactory: null, activeHistoryQueries: activeQueries);
    }

    private static void CreateLegacyDatabase(string path, int rows, bool thenDelete)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA auto_vacuum=NONE; PRAGMA journal_mode=DELETE;";
            pragma.ExecuteNonQuery();
        }
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE payload(id INTEGER PRIMARY KEY, blob BLOB);";
            create.ExecuteNonQuery();
        }
        using (var tx = connection.BeginTransaction())
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO payload(blob) VALUES ($blob);";
            var blob = insert.CreateParameter();
            blob.ParameterName = "$blob";
            insert.Parameters.Add(blob);
            var payload = new byte[32 * 1024];
            Array.Fill(payload, (byte)7);
            for (var i = 0; i < rows; i++)
            {
                blob.Value = payload;
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (thenDelete)
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM payload;";
            delete.ExecuteNonQuery();
        }
    }
}
