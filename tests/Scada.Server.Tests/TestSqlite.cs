using Microsoft.Data.Sqlite;

namespace Scada.Server.Tests;

/// <summary>
/// 只清「自己这一组数据库」的连接池。
///
/// 原先各处用的是 <see cref="SqliteConnection.ClearAllPools"/>，它是进程级的：一个夹具结束时
/// 会把并行运行的其他夹具的连接一起清掉，对方下一次查询就直接拿到已关闭的连接
/// （表现为几毫秒内的莫名失败，单独跑却全绿）。这里按数据目录/文件精确清理。
///
/// 注意池是按「数据源」分组的，而一个站点目录里不止三个库：history/ 下按年月分片的
/// raw_202611.db、agg_2026.db，以及 backups / diag / reports 里的库同样在池里。只清
/// config.db / runtime.db / scada.db 会漏掉分片库，句柄不释放 → Directory.Delete 抛 IOException
/// → 调用方普遍 catch 后忽略 → 每个用例在系统临时目录里永久留下一个目录。
/// 本机在修这一处之前累计到 6809 个目录 / 17079 个文件 / 8.22 GB。
/// </summary>
internal static class TestSqlite
{
    /// <summary>清掉该目录（递归）下所有 *.db 的连接池；传单个文件时只清它自己。</summary>
    public static void ClearPools(string directoryOrFile)
    {
        if (File.Exists(directoryOrFile)) { ClearOne(directoryOrFile); return; }

        // 目录可能因为「创建失败 / 已被删掉」而不存在，此时仍按三个通用名清一遍，
        // 避免池里残留的连接一直握着已删除文件的句柄。
        if (!Directory.Exists(directoryOrFile))
        {
            foreach (var name in new[] { "config.db", "runtime.db", "scada.db" })
                ClearOne(Path.Combine(directoryOrFile, name));
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directoryOrFile, "*.db", SearchOption.AllDirectories))
            ClearOne(file);
    }

    /// <summary>
    /// 删除临时数据目录：先按库清池，再退避重试；仍然失败则登记到进程退出时兜底清理。
    ///
    /// Windows 上目录里只要还有一个句柄（后台服务、池里的 SqliteConnection、杀毒扫描），
    /// Delete 就会抛 IOException。实测即使是 ClearPool 之后，主库文件的句柄偶尔仍被池中
    /// 正在关闭的连接持有，几次重试都删不掉——如果就这样放弃，每个用例都会在系统临时目录
    /// 留下一个目录（本机曾累计 6809 个 / 8.22 GB）。所以最后一步是登记，等进程退出、
    /// 所有句柄确定释放后统一清理。
    /// </summary>
    public static void TryDeleteDirectory(string directory)
    {
        // 先给之前删不掉的目录一次机会：宿主的 Kestrel / DB 连接是在 Dispose 之后
        // 才真正关闭的，并行跑时这段延迟更长，当时删不掉、几秒后就删得掉了。
        RetryDeferred();

        ClearPools(directory);
        if (DeleteDirectoryWithBackoff(directory)) return;

        // 清池后仍然删不掉：排进重试队列，并登记到进程退出兜底。
        ClearPools(directory);
        if (Deferred.Count < MaxDeferred) Deferred.Enqueue(directory);
        TempCleanup.Track(directory);
    }

    /// <summary>
    /// 夹具收尾专用：宿主停止后文件句柄的关闭是异步的，先给一个很短的窗口
    /// （实测并行跑时 2 秒内也释放不了，长窗口只是拖慢整轮），删不掉就交给
    /// <see cref="TempCleanup"/> 的跨进程清单，下一次运行必然能清掉。
    /// </summary>
    public static async Task TryDeleteDirectoryAsync(string directory, int timeoutMs = 200)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            TryDeleteDirectory(directory);
            if (!Directory.Exists(directory)) return;
            if (Environment.TickCount64 >= deadline) return;
            await Task.Delay(50);
        }
    }

    private const int MaxDeferred = 200;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Deferred = new();

    /// <summary>每次清理顺手重试几个之前失败的目录；不阻塞当前夹具。</summary>
    private static void RetryDeferred()
    {
        for (var i = 0; i < 3 && Deferred.TryDequeue(out var path); i++)
        {
            if (!Directory.Exists(path)) continue;
            if (!DeleteDirectoryWithBackoff(path)) Deferred.Enqueue(path);
        }
    }

    private static bool DeleteDirectoryWithBackoff(string directory)
    {
        var backoffMs = 20;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) return true;
                Directory.Delete(directory, true);
                return true;
            }
            catch (IOException) { Thread.Sleep(backoffMs); }
            catch (UnauthorizedAccessException) { Thread.Sleep(backoffMs); }
            backoffMs *= 2;
        }
        return !Directory.Exists(directory);
    }

    /// <summary>
    /// 删除单个临时库文件（.db 及其 -wal / -shm）。
    /// 单次 File.Delete 在 Windows 上会和夹具释放句柄抢时间，失败即永久残留。
    /// </summary>
    public static void TryDeleteFile(string path)
    {
        ClearOne(path);
        var backoffMs = 20;
        var remaining = false;
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (!File.Exists(file)) break;
                    File.Delete(file);
                    break;
                }
                catch (IOException) { Thread.Sleep(backoffMs); }
                catch (UnauthorizedAccessException) { Thread.Sleep(backoffMs); }
                backoffMs *= 2;
            }
            if (File.Exists(file)) remaining = true;
        }
        if (remaining) TempCleanup.TrackFile(path);
    }

    private static void ClearOne(string path)
    {
        var full = Path.GetFullPath(path);

        // ClearPool 按连接串定位池，而各测试建库时的写法并不统一（有的显式 Pooling=False，
        // 有的用默认值）。把常见的几种写法都清一遍：清不存在的池是 no-op，没有副作用。
        foreach (var connectionString in new[]
                 {
                     $"Data Source={full}",
                     $"Data Source={full};Pooling=True",
                     $"Data Source={full};Pooling=False",
                     new SqliteConnectionStringBuilder { DataSource = full, Pooling = true }.ToString(),
                 })
        {
            try
            {
                // 连接不需要 Open：ClearPool 按数据源找到对应的池并释放其中的连接。
                using var connection = new SqliteConnection(connectionString);
                SqliteConnection.ClearPool(connection);
            }
            catch (Exception)
            {
                // 清理失败不该让业务断言变红；残留的空池会在进程结束时释放。
            }
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string prefix = "scada-server-test-")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose() => TestSqlite.TryDeleteDirectory(Path);
}
