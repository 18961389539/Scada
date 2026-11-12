using Microsoft.Data.Sqlite;

namespace Scada.Server.Tests;

/// <summary>
/// 只清「自己这一组数据库」的连接池。
///
/// 原先各处用的是 <see cref="SqliteConnection.ClearAllPools"/>，它是进程级的：一个夹具结束时
/// 会把并行运行的其他夹具的连接一起清掉，对方下一次查询就直接拿到已关闭的连接
/// （表现为几毫秒内的莫名失败，单独跑却全绿）。这里按数据目录/文件精确清理。
/// </summary>
internal static class TestSqlite
{
    public static void ClearPools(string directoryOrFile)
    {
        if (File.Exists(directoryOrFile)) { ClearOne(directoryOrFile); return; }
        foreach (var name in new[] { "config.db", "runtime.db", "scada.db" })
            ClearOne(Path.Combine(directoryOrFile, name));
    }

    /// <summary>
    /// 删除临时数据目录：先按库清池，再重试几次。
    /// Windows 上目录里只要还有一个句柄（后台服务、未释放的 SqliteConnection），Delete 就会抛 IOException；
    /// 清理失败不该让业务断言变红，但也不该无限重试——3 次之后放弃，留给系统临时目录。
    /// </summary>
    public static void TryDeleteDirectory(string directory)
    {
        ClearPools(directory);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                Directory.Delete(directory, true);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    private static void ClearOne(string path)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(path),
                Pooling = true
            }.ToString());
            // 连接不需要 Open：ClearPool 按连接串找到对应的池并释放其中的连接。
            SqliteConnection.ClearPool(connection);
        }
        catch (Exception)
        {
            // 清理失败不该让业务断言变红；残留的空池会在进程结束时释放。
        }
    }
}
