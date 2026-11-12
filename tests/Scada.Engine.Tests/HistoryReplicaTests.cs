using Microsoft.Data.Sqlite;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

/// <summary>
/// 历史主备复制的两条流：行流按 rowid，改写流把原地更新过的行重新送一次。
/// 没有改写流时，主机对已写点的质量修正永远不会到达备机（rowid 不变、旧实现还是 DO NOTHING）。
/// </summary>
public sealed class HistoryReplicaTests
{
    private const string File = "raw_202611.db";
    private static readonly string[] Columns = ["TagId", "Ts", "Value", "Quality", "Text"];

    [Fact]
    public void Replicated_rows_follow_the_same_quality_precedence_as_the_host()
    {
        using var db = Open();
        CreateSchema(db);
        Exec(db, "INSERT INTO HistoryValue (TagId, Ts, Value, Quality) VALUES (1, 1000, 5.0, 0)");

        ApplyRow(db, "HistoryValue", ["1", "1", "1000", "6.0", "2", null]); // CommFailure 覆盖 Good
        Assert.Equal(6.0, Scalar(db, "SELECT Value FROM HistoryValue WHERE TagId = 1 AND Ts = 1000"));
        Assert.Equal(2L, Scalar(db, "SELECT Quality FROM HistoryValue WHERE TagId = 1 AND Ts = 1000"));

        ApplyRow(db, "HistoryValue", ["2", "1", "1000", "7.0", "3", null]); // Uncertain 不能洗白 CommFailure
        Assert.Equal(6.0, Scalar(db, "SELECT Value FROM HistoryValue WHERE TagId = 1 AND Ts = 1000"));
        Assert.Equal(2L, Scalar(db, "SELECT Quality FROM HistoryValue WHERE TagId = 1 AND Ts = 1000"));
    }

    [Fact]
    public void Rewrite_stream_replays_corrections_the_rowid_cursor_cannot_see()
    {
        using var db = Open();
        CreateSchema(db);
        Exec(db, "INSERT INTO HistoryValue (TagId, Ts, Value, Quality) VALUES (1, 1000, 5.0, 0)");

        var first = SqliteHistoryReplica.Read(db, File, "HistoryValue", 0, 100);
        var cursor = long.Parse(first.Rows[^1][0]!);

        // 主机质量修正：同一主键原地更新，rowid 不变。
        Exec(db, "UPDATE HistoryValue SET Value = 5.5, Quality = 2 WHERE TagId = 1 AND Ts = 1000");

        Assert.Empty(SqliteHistoryReplica.Read(db, File, "HistoryValue", cursor, 100).Rows);

        var rewrite = SqliteHistoryReplica.Read(db, File, SqliteHistoryReplica.RewriteTable, 0, 100);
        var row = Assert.Single(rewrite.Rows);
        Assert.Equal(1, long.Parse(row[0]!));      // 改写序号作为游标
        Assert.Equal(1, long.Parse(row[1]!));      // TagId
        Assert.Equal(1000, long.Parse(row[2]!));   // Ts
        Assert.Equal(5.5, double.Parse(row[3]!, System.Globalization.CultureInfo.InvariantCulture)); // 当前 Value
        Assert.Equal(2, long.Parse(row[4]!));      // 当前 Quality
    }

    [Fact]
    public void Applying_the_rewrite_stream_updates_the_replica_row()
    {
        using var db = Open();
        CreateSchema(db);
        Exec(db, "INSERT INTO HistoryValue (TagId, Ts, Value, Quality) VALUES (1, 1000, 5.0, 0)");

        ApplyRow(db, SqliteHistoryReplica.RewriteTable, ["1", "1", "1000", "5.5", "2", null]);

        Assert.Equal(5.5, Scalar(db, "SELECT Value FROM HistoryValue WHERE TagId = 1 AND Ts = 1000"));
        Assert.Equal(2L, Scalar(db, "SELECT Quality FROM HistoryValue WHERE TagId = 1 AND Ts = 1000"));
    }

    [Fact]
    public void Text_column_round_trips_through_replica_rows()
    {
        using var db = Open();
        CreateSchema(db);
        Exec(db, "INSERT INTO HistoryValue (TagId, Ts, Value, Quality, Text) VALUES (2, 2000, 0, 0, 'SN-42')");

        var read = SqliteHistoryReplica.Read(db, File, "HistoryValue", 0, 100);
        var row = Assert.Single(read.Rows);
        Assert.Equal("SN-42", row[5]);

        ApplyRow(db, "HistoryValue", ["3", "2", "2000", "0", "0", "SN-42"]);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Text FROM HistoryValue WHERE TagId = 2 AND Ts = 2000";
        Assert.Equal("SN-42", cmd.ExecuteScalar());
    }

    private static void ApplyRow(SqliteConnection db, string table, string?[] row) =>
        SqliteHistoryReplica.Apply(db, table, new HistoryReplicaRows(File, table, [row], Columns));

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void CreateSchema(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE HistoryValue (
                TagId INTEGER NOT NULL,
                Ts INTEGER NOT NULL,
                Value REAL NOT NULL,
                Quality INTEGER NOT NULL,
                Text TEXT,
                PRIMARY KEY (TagId, Ts));
            CREATE TABLE HistoryValueRewrite (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TagId INTEGER NOT NULL,
                Ts INTEGER NOT NULL);
            CREATE TRIGGER HistoryValue_CaptureRewrite AFTER UPDATE ON HistoryValue
            BEGIN
                INSERT INTO HistoryValueRewrite(TagId, Ts) VALUES (OLD.TagId, OLD.Ts);
                DELETE FROM HistoryValueRewrite
                WHERE Id < (SELECT MAX(Id) FROM HistoryValueRewrite) - 100000;
            END;
            """);
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static double Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDouble(cmd.ExecuteScalar());
    }
}
