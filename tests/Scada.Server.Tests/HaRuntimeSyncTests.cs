using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Ha;

namespace Scada.Server.Tests;

public sealed class HaRuntimeSyncTests
{
    [Fact]
    public void IncrementalSyncCopiesInsertsUpdatesAndDeletesAndIsReentrant()
    {
        using var dir = new TempDir();
        var primaryPath = Path.Combine(dir.Path, "primary.db");
        var standbyPath = Path.Combine(dir.Path, "standby.db");
        Migrate(primaryPath); Migrate(standbyPath);

        using var primary = Open(primaryPath);
        using var standby = Open(standbyPath);
        HaRuntimeSync.EnsureChangeFeed(primary);
        HaRuntimeSync.EnsureChangeFeed(standby);
        Assert.Equal(0, HaRuntimeSync.Watermark(primary));

        InsertLog(primary, 1, "create");
        InsertLog(primary, 2, "update");
        var first = HaRuntimeSync.Read(primary, 0, 100);
        Assert.Equal(2, first.Changes.Count);
        Assert.False(first.More);
        // 运行表有大量可空列；序列化必须是 JSON null，否则 /runtime-changes 会 500，整段复制（含历史）被中断。
        var payload = System.Text.Json.JsonSerializer.Serialize(first);
        Assert.DoesNotContain("Undefined", payload, StringComparison.Ordinal);
        first = System.Text.Json.JsonSerializer.Deserialize<HaRuntimeChanges>(payload)!;
        var through = HaRuntimeSync.Apply(standby, first);
        Assert.Equal(first.Through, through);
        Assert.Equal(2, CountLogs(standby));

        // 重放同一批：按主键覆盖，行数不变。
        HaRuntimeSync.Apply(standby, first);
        Assert.Equal(2, CountLogs(standby));

        Exec(primary, "UPDATE OperationLog SET Action = 'renamed' WHERE Id = 2");
        var second = HaRuntimeSync.Read(primary, first.Through, 100);
        Assert.Single(second.Changes);
        HaRuntimeSync.Apply(standby, second);
        Assert.Equal("renamed", Text(standby, "SELECT Action FROM OperationLog WHERE Id = 2"));

        Exec(primary, "DELETE FROM OperationLog WHERE Id = 1");
        var third = HaRuntimeSync.Read(primary, second.Through, 100);
        Assert.True(Assert.Single(third.Changes).Deleted);
        HaRuntimeSync.Apply(standby, third);
        Assert.Equal(1, CountLogs(standby));
        Assert.Equal(0, Scalar(standby, "SELECT COUNT(*) FROM OperationLog WHERE Id = 1"));
        Assert.Equal(HaRuntimeSync.Watermark(primary), third.Through);
    }

    [Fact]
    public void ConfigSnapshotHashDoesNotChangeWhenRuntimeRowsAreWritten()
    {
        using var dir = new TempDir();
        var configPath = Path.Combine(dir.Path, "config.db");
        var runtimePath = Path.Combine(dir.Path, "runtime.db");
        using (var db = new ConfigDbContext(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={configPath};Pooling=False").Options))
            db.Database.Migrate();
        Migrate(runtimePath);

        var before = SnapshotHash(configPath, dir.Path, "a");
        using (var runtime = Open(runtimePath))
        {
            HaRuntimeSync.EnsureChangeFeed(runtime);
            InsertLog(runtime, 1, "runtime-only");
        }
        var after = SnapshotHash(configPath, dir.Path, "b");
        Assert.Equal(before, after);

        using (var config = new SqliteConnection($"Data Source={configPath};Pooling=False"))
        {
            config.Open();
            Exec(config, "CREATE TABLE IF NOT EXISTS Probe (Id INTEGER PRIMARY KEY, Name TEXT)");
            Exec(config, "INSERT INTO Probe (Id, Name) VALUES (1, 'changed')");
        }
        Assert.NotEqual(before, SnapshotHash(configPath, dir.Path, "c"));
    }

    [Fact]
    public void RegularManifestDoesNotNeedARuntimeSnapshotHash()
    {
        // 契约：日常清单只带变更水位，RuntimeSha 为空，避免主机每个 ConfigSyncMs 都 BackupDatabase 整个运行库。
        var manifest = new HaManifest("n", "Primary", 1, "schema", "sha", 10, [], RuntimeSha: null, RuntimeBytes: null, RuntimeWatermark: 42);
        Assert.Null(manifest.RuntimeSha);
        Assert.Null(manifest.RuntimeBytes);
        Assert.Equal(42, manifest.RuntimeWatermark);
    }

    private static void Migrate(string path)
    {
        using var db = new RuntimeDbContext(new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        db.Database.Migrate();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void InsertLog(SqliteConnection connection, long id, string action)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO OperationLog (Id, Ts, UserName, Action, Result) VALUES ($id, $ts, 'tester', $action, 'ok')";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", id * 1000);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.ExecuteNonQuery();
    }

    private static long CountLogs(SqliteConnection connection) => Scalar(connection, "SELECT COUNT(*) FROM OperationLog");

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is string s ? Convert.ToInt64(s) : Convert.ToInt64(value);
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static string SnapshotHash(string source, string dir, string name)
    {
        var target = Path.Combine(dir, $"snap-{name}.db");
        using (var from = new SqliteConnection($"Data Source={source};Pooling=False"))
        using (var to = new SqliteConnection($"Data Source={target};Pooling=False"))
        {
            from.Open(); to.Open(); from.BackupDatabase(to);
        }
        return HaReplication.Hash(target);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scada-ha-rt-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            TestSqlite.ClearPools(Path);
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
        }
    }
}
