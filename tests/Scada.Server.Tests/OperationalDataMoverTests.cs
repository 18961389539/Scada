using Microsoft.Data.Sqlite;
using Scada.Data.Infrastructure;

namespace Scada.Server.Tests;

// 运行表从 config.db 搬到 runtime.db 必须可重入：跨库事务在 WAL 下不保证原子提交，
// 中途失败可能只提交了前几张表。这组测试锁住"重跑不冲突、不重复、行数对得上"。
public sealed class OperationalDataMoverTests
{
    [Fact]
    public void RerunningTheMoveIsIdempotentAndBackfillsLostRows()
    {
        using var dir = new TempDir();
        var configPath = Path.Combine(dir.Path, "scada.db");
        using var connection = new SqliteConnection($"Data Source={configPath};Pooling=False");
        connection.Open();
        Execute(connection, Table);
        Execute(connection, "INSERT INTO AlarmRollupState (Id, WatermarkTs, LastError) VALUES (1, 100, 'a'), (2, 200, 'b')");

        Assert.True(Move(connection));

        using var runtime = new SqliteConnection($"Data Source={Path.Combine(dir.Path, "runtime.db")};Pooling=False");
        runtime.Open();
        Assert.Equal(2, Scalar(runtime, "SELECT COUNT(*) FROM AlarmRollupState"));

        // 第二次执行：标记已写但源表仍有数据（回滚后再升级的场景），必须重新复制且不能主键冲突。
        Assert.True(Move(connection));
        Assert.Equal(2, Scalar(runtime, "SELECT COUNT(*) FROM AlarmRollupState"));

        // 模拟上一次只提交了一部分：删掉一行后重跑应补齐。
        Execute(runtime, "DELETE FROM AlarmRollupState WHERE Id = 2");
        Assert.True(Move(connection));
        Assert.Equal(2, Scalar(runtime, "SELECT COUNT(*) FROM AlarmRollupState"));
        Assert.Equal(1, Scalar(runtime, "SELECT COUNT(*) FROM OperationalImport"));
    }

    [Fact]
    public void MoveIsSkippedWhenSourceTablesAreAlreadyGone()
    {
        using var dir = new TempDir();
        var configPath = Path.Combine(dir.Path, "scada.db");
        using var connection = new SqliteConnection($"Data Source={configPath};Pooling=False");
        connection.Open();
        Execute(connection, Table);
        Execute(connection, "INSERT INTO AlarmRollupState (Id, WatermarkTs) VALUES (1, 100)");
        Assert.True(Move(connection));

        // 迁移删掉源表后再跑一次（EF 重放迁移的情形）：没有源表就什么都不做，不报错。
        Execute(connection, "DROP TABLE AlarmRollupState");
        Assert.True(Move(connection));
    }

    // 用 AlarmRollupState 做样本：除主键外全部可空，不受 runtime 表其它 NOT NULL 约束影响。
    private const string Table = "CREATE TABLE AlarmRollupState (Id INTEGER NOT NULL CONSTRAINT PK_AlarmRollupState PRIMARY KEY, WatermarkTs INTEGER NULL, LastRunTs INTEGER NULL, RebuildFromTs INTEGER NULL, LastError TEXT NULL)";

    private static bool Move(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 'scada_move_operational' AS scada_move";
        return OperationalDataMover.TryApply(command);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scada-mover-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* 句柄释放失败不影响断言 */ }
        }
    }
}
