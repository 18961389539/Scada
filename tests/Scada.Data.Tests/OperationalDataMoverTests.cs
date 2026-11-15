using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scada.Data.Infrastructure;

namespace Scada.Data.Tests;

/// <summary>
/// 运行表搬迁是"升级可重入"的：上一次只拷了一半的表，重试要能接着拷。
/// 这里锁定两个曾经踩过的坑：重试不能因主键冲突卡死，也不能借 INSERT OR REPLACE 的删除语义
/// 把父行的子表数据级联删掉；另外搬迁目标必须是运行库真正打开的那个文件。
/// </summary>
public sealed class OperationalDataMoverTests
{
    [Fact]
    public void RetryKeepsChildRowsOfAlreadyCopiedParents()
    {
        using var root = new TempRoot();
        var configPath = root.Path("scada.db");
        var runtimePath = root.Path("runtime.db");
        Migrate(configPath);
        Migrate(runtimePath);
        SeedAlarm(configPath);

        using var connection = OpenWriter(configPath);
        Assert.True(Move(connection));

        var runtime = Open(runtimePath);
        Assert.Equal(1, TempRoot.Scalar(runtime, "SELECT COUNT(*) FROM \"AlarmEvent\""));
        Assert.Equal(2, TempRoot.Scalar(runtime, "SELECT COUNT(*) FROM \"AlarmActivity\""));

        // 第二次搬迁：源表还在（模拟"标记已写但删表没跑完"的回滚态）。
        // INSERT OR REPLACE 会删掉 AlarmEvent 行并级联清空 AlarmActivity。
        Assert.True(Move(connection));
        runtime.Close();
        using var again = Open(runtimePath);
        Assert.Equal(1, TempRoot.Scalar(again, "SELECT COUNT(*) FROM \"AlarmEvent\""));
        Assert.Equal(2, TempRoot.Scalar(again, "SELECT COUNT(*) FROM \"AlarmActivity\""));
    }

    [Fact]
    public void MoveTargetsTheRegisteredRuntimePathNotTheConfigSibling()
    {
        using var root = new TempRoot();
        var configPath = root.Path("data/scada.db");
        var runtimePath = root.Path("data/runtime.db");
        var customPath = root.Path("other/custom-runtime.db");
        Migrate(configPath);
        Migrate(runtimePath);
        Migrate(customPath);
        SeedAlarm(configPath);

        // 运维把 Scada:RuntimeDbPath 指到别的盘：搬迁必须跟着走。
        OperationalDataMover.RegisterRuntimePath(configPath, customPath);

        using var connection = OpenWriter(configPath);
        Assert.True(Move(connection));

        using var custom = Open(customPath);
        Assert.Equal(1L, TempRoot.Scalar(custom, "SELECT COUNT(*) FROM \"AlarmEvent\""));
        // 同目录那个文件只应该剩空表（Migrate 建的），数据不能落进去。
        using var sibling = Open(runtimePath);
        Assert.Equal(0L, TempRoot.Scalar(sibling, "SELECT COUNT(*) FROM \"AlarmEvent\""));
    }

    [Fact]
    public void MoveIsSkippedForInMemoryDatabases()
    {
        using var root = new TempRoot();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "-- scada_move_operational";
        // 内存库没有可附加的文件，只把语句替换掉，不抛异常。
        Assert.True(OperationalDataMover.TryApply(command));
        Assert.Equal("SELECT 1", command.CommandText);
    }

    private static void Migrate(string path)
    {
        var options = new DbContextOptionsBuilder<RuntimeDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString)
            .Options;
        using var db = new RuntimeDbContext(options);
        db.Database.Migrate();
    }

    private static void SeedAlarm(string path)
    {
        using var connection = Open(path);
        TempRoot.Exec(connection, """
            INSERT INTO "AlarmEvent" ("Level","Category","Consequence","ResponseTimeSec","Value","StartTs","Message","Shelved","Suppressed","Chattering","FirstOut","FirstUp")
            VALUES (1, 0, 0, 0, 0.5, 1000, 'existing', 0, 0, 0, 0, 0);
            INSERT INTO "AlarmActivity" ("AlarmEventId","Ts","Action") VALUES (1, 1100, 'ack'), (1, 1200, 'shelve');
            """);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenWriter(string path)
    {
        var connection = Open(path);
        // 外键必须真的生效，级联删除才会发生——这正是要防的场景。
        TempRoot.Exec(connection, "PRAGMA foreign_keys=ON");
        return connection;
    }

    private static bool Move(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "-- scada_move_operational";
        return OperationalDataMover.TryApply(command);
    }
}
