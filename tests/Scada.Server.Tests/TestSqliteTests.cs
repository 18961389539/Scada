using Microsoft.Data.Sqlite;

namespace Scada.Server.Tests;

/// <summary>
/// 夹具清理逻辑自身的回归。
///
/// 背景：站点夹具的数据库不止 config.db / runtime.db，history 下还有按年月分片的
/// raw_*.db。清理时只按三个固定文件名清池，分片库的句柄仍在，Directory.Delete 抛
/// IOException 又被调用方吞掉，于是每个用例在系统临时目录里留下一个目录。
/// 本机修这一处之前累计到 6809 个目录 / 17079 个文件 / 8.22 GB。
/// </summary>
public sealed class TestSqliteTests
{
    [Fact]
    public void TryDeleteDirectoryReleasesPooledWalConnectionsIncludingHistoryShards()
    {
        // 用独立前缀，避免与并行会话的 scada-authz / scada-center 目录混淆。
        using var temp = new TempDir("scada-cleanup-test-");
        var historyDirectory = Path.Combine(temp.Path, "history");
        Directory.CreateDirectory(historyDirectory);

        var configPath = Path.Combine(temp.Path, "config.db");
        var shardPath = Path.Combine(historyDirectory, "raw_202611.db");

        // 两种池化写法都覆盖：config 显式不带 Pooling（服务端 DI 的写法），
        // 历史分片用默认值（Pooling=True）。WAL 模式下文件句柄在连接 Dispose 后
        // 仍由池持有，这正是删除失败的原因。
        CreateWalDatabase(configPath, "Data Source=" + configPath);
        CreateWalDatabase(shardPath, "Data Source=" + shardPath);

        Assert.True(File.Exists(configPath));
        Assert.True(File.Exists(shardPath), "历史分片库应已创建");
        Assert.True(File.Exists(shardPath + "-wal"), "分片库应处于 WAL 模式，否则这条用例测不到句柄");

        TestSqlite.TryDeleteDirectory(temp.Path);

        Assert.False(Directory.Exists(temp.Path), "清理后目录不应残留");
    }

    [Fact]
    public void ClearPoolsWalksNestedDatabaseFiles()
    {
        using var temp = new TempDir("scada-cleanup-test-");
        var nested = Path.Combine(temp.Path, "reports", "2026");
        Directory.CreateDirectory(nested);
        var nestedPath = Path.Combine(nested, "agg_2026.db");
        CreateWalDatabase(nestedPath, "Data Source=" + nestedPath);

        // ClearPools 必须递归枚举，否则两级子目录里的库会被漏掉。
        TestSqlite.ClearPools(temp.Path);

        // 清池后句柄应已释放，可以直接删掉单个文件（不经过 TryDeleteDirectory 的重试）。
        File.Delete(nestedPath);
        Assert.False(File.Exists(nestedPath));
    }

    [Fact]
    public void UndeletableDirectoryIsDeferredToProcessExitInsteadOfLeaking()
    {
        using var temp = new TempDir("scada-cleanup-test-");
        var locked = Path.Combine(temp.Path, "locked.db");
        File.WriteAllText(locked, "x");

        // 独占句柄模拟「池中连接还没关闭」：此时无论重试几次都删不掉。
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            TestSqlite.TryDeleteDirectory(temp.Path);
            Assert.True(Directory.Exists(temp.Path), "句柄未释放时目录应删不掉（这正是泄漏的前提）");
            Assert.True(TempCleanup.PendingCount > 0, "删不掉的目标必须登记到进程退出兜底，而不是直接放弃");
        }

        // 句柄释放后，进程退出时的兜底必须能真的清掉。
        TempCleanup.Flush();
        Assert.False(Directory.Exists(temp.Path), "兜底清理后目录不应残留");
    }

    [Fact]
    public void PendingListLeftByPreviousRunIsDrainedOnStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scada-cleanup-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config.db"), "x");

        var listPath = TempCleanup.PendingListPath;
        var backup = File.Exists(listPath) ? File.ReadAllText(listPath) : null;
        try
        {
            // 模拟上一个 testhost 退出时留下的清单：那时句柄已释放，下一次启动必须能删掉。
            File.WriteAllLines(listPath, [directory]);

            TempCleanup.DrainPendingFromPreviousRun();

            Assert.False(Directory.Exists(directory), "上一次运行遗留的目录应在本次启动时清掉");
            Assert.False(File.Exists(listPath), "清理完成后清单文件应被删除");
        }
        finally
        {
            if (backup is not null) File.WriteAllText(listPath, backup);
            else if (File.Exists(listPath)) File.Delete(listPath);
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
        }
    }

    private static void CreateWalDatabase(string path, string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE sample(value INTEGER); INSERT INTO sample VALUES(1);";
        command.ExecuteNonQuery();
    }
}
