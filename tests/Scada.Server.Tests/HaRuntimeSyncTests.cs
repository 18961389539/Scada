using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scada.Server.Ha;

namespace Scada.Server.Tests;

public sealed class HaRuntimeSyncTests
{
    [Fact]
    public void Applying_replicated_changes_does_not_append_to_the_local_feed()
    {
        using var db = Open();
        Seed(db);
        HaRuntimeSync.EnsureChangeFeed(db);
        Exec(db, "INSERT INTO T(Id, V) VALUES (1, 'local')");
        Assert.Equal(1, HaRuntimeSync.Watermark(db));

        HaRuntimeSync.Apply(db, Batch(2, "Id=2", 2, "replica"));

        // 回放写入了行，但不应该改变本机变更水位（否则备机 feed 会无限增长，切换后互相回灌）。
        Assert.Equal(1, HaRuntimeSync.Watermark(db));
        using var read = db.CreateCommand();
        read.CommandText = "SELECT V FROM T WHERE Id = 2";
        Assert.Equal("replica", read.ExecuteScalar());
    }

    [Fact]
    public void PrepareReplica_drops_triggers_so_replay_is_not_captured()
    {
        using var db = Open();
        Seed(db);
        HaRuntimeSync.EnsureChangeFeed(db);
        HaRuntimeSync.PrepareReplica(db);

        Exec(db, "INSERT INTO T(Id, V) VALUES (3, 'while-standby')");

        Assert.Equal(0, HaRuntimeSync.Watermark(db));
    }

    [Fact]
    public void Read_flags_a_gap_when_the_cursor_falls_out_of_the_retained_window()
    {
        using var db = Open();
        Seed(db);
        HaRuntimeSync.EnsureChangeFeed(db);
        for (var i = 1; i <= 5; i++) Exec(db, $"INSERT INTO T(Id, V) VALUES ({i}, 'v{i}')");
        Assert.Equal(5, HaRuntimeSync.Watermark(db));

        HaRuntimeSync.Trim(db, 4);

        var gapped = HaRuntimeSync.Read(db, 1, 100);
        Assert.True(gapped.Gap);
        Assert.Equal(4, gapped.Oldest);

        var continuous = HaRuntimeSync.Read(db, 3, 100);
        Assert.False(continuous.Gap);
    }

    [Fact]
    public void Legacy_triggers_without_guard_are_rebuilt_once()
    {
        using var db = Open();
        Seed(db);
        // 模拟旧版本：只有变更表与不带 Applying 守卫的触发器。
        Exec(db, "CREATE TABLE HaRuntimeChange (Id INTEGER PRIMARY KEY AUTOINCREMENT, T TEXT NOT NULL, P TEXT NOT NULL, D INTEGER NOT NULL DEFAULT 0)");
        Exec(db, "CREATE TRIGGER ha_ai_T AFTER INSERT ON T BEGIN INSERT INTO HaRuntimeChange(T, P, D) VALUES ('T', 'Id=' || new.Id, 0); END");
        Exec(db, "INSERT INTO T(Id, V) VALUES (1, 'old')");
        Assert.Equal(1, HaRuntimeSync.Watermark(db));

        HaRuntimeSync.EnsureChangeFeed(db);
        HaRuntimeSync.Apply(db, Batch(2, "Id=2", 2, "replica"));
        Assert.Equal(1, HaRuntimeSync.Watermark(db));

        // 重建后的触发器仍要记录本机真实写入。
        Exec(db, "INSERT INTO T(Id, V) VALUES (3, 'new')");
        Assert.Equal(2, HaRuntimeSync.Watermark(db));
    }

    [Fact]
    public void ReplayRejectsInjectedTableNamesKeysAndColumns()
    {
        using var db = Open();
        Seed(db);
        HaRuntimeSync.EnsureChangeFeed(db);
        Exec(db, "INSERT INTO T(Id, V) VALUES (1, 'keep')");

        // peer 失陷或响应被伪造时，Table / Key / 列名都可能带 SQL；必须在拼语句前拒绝。
        Assert.Throws<InvalidOperationException>(() => ApplyDelete(db, "T\" WHERE 1=1 --", "Id=1"));
        Assert.Throws<InvalidOperationException>(() => ApplyDelete(db, "T", "Id=1 OR 1=1"));
        Assert.Throws<InvalidOperationException>(() => ApplyDelete(db, "T", "Id=1; DROP TABLE T"));
        Assert.Throws<InvalidOperationException>(() => ApplyDelete(db, "T", "1=1"));
        Assert.Throws<InvalidOperationException>(() => ApplyDelete(db, "NoSuchTable", "Id=1"));
        Assert.Throws<InvalidOperationException>(() => ApplyDelete(db, "HaSyncState", "Id=1"));
        Assert.Throws<InvalidOperationException>(() => HaRuntimeSync.Apply(db, new HaRuntimeChanges(0, 1, false,
            [new HaRuntimeChangeRow(1, "T", "Id=1", false,
                new Dictionary<string, JsonElement> { ["V\" , 1) --"] = JsonSerializer.SerializeToElement("x") })])));

        using var read = db.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM T";
        Assert.Equal(1L, read.ExecuteScalar());
        // Applying 标记写在事务里，整批回滚后不能把备机永久卡在「正在回放」。
        using var applying = db.CreateCommand();
        applying.CommandText = "SELECT Applying FROM HaSyncState WHERE Id = 1";
        Assert.Equal(0L, applying.ExecuteScalar());
    }

    [Fact]
    public void CompositeAndQuotedKeysAreReplayedThroughParameters()
    {
        using var db = Open();
        Exec(db, "CREATE TABLE Pair (Id INTEGER, Slot TEXT, V TEXT, PRIMARY KEY(Id, Slot))");
        HaRuntimeSync.EnsureChangeFeed(db);
        var row = new Dictionary<string, JsonElement>
        {
            ["Id"] = JsonSerializer.SerializeToElement(2),
            ["Slot"] = JsonSerializer.SerializeToElement("a'b"),
            ["V"] = JsonSerializer.SerializeToElement("replica")
        };
        const string key = "Id=2 AND Slot='a''b'";
        HaRuntimeSync.Apply(db, new HaRuntimeChanges(0, 1, false, [new HaRuntimeChangeRow(1, "Pair", key, false, row)]));

        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT V FROM Pair WHERE Id = 2 AND Slot = 'a''b'";
            Assert.Equal("replica", read.ExecuteScalar());
        }
        HaRuntimeSync.Apply(db, new HaRuntimeChanges(1, 2, false, [new HaRuntimeChangeRow(2, "Pair", key, true, null)]));
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT COUNT(*) FROM Pair";
            Assert.Equal(0L, read.ExecuteScalar());
        }
    }

    private static void ApplyDelete(SqliteConnection db, string table, string key) =>
        HaRuntimeSync.Apply(db, new HaRuntimeChanges(0, 1, false, [new HaRuntimeChangeRow(1, table, key, true, null)]));

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Seed(SqliteConnection db) =>
        Exec(db, "CREATE TABLE T (Id INTEGER PRIMARY KEY, V TEXT NOT NULL)");

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static HaRuntimeChanges Batch(long through, string key, long id, string value) =>
        new(1, through, false, [new HaRuntimeChangeRow(through, "T", key, false, Row(id, value))]);

    private static IReadOnlyDictionary<string, JsonElement> Row(long id, string value) =>
        new Dictionary<string, JsonElement>
        {
            ["Id"] = JsonSerializer.SerializeToElement(id),
            ["V"] = JsonSerializer.SerializeToElement(value)
        };
}
