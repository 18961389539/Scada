using System.Collections.Concurrent;

namespace Scada.Tests;

/// <summary>
/// 临时目录的最后一道兜底。
///
/// 夹具里「清池 → 删除」通常能成功，但在 Windows 上确实存在删不掉的窗口：池中连接正在关闭时
/// 主库文件的句柄还没放掉，几次重试都拿到 IOException。此时如果直接放弃，目录就永久留在
/// 系统临时目录里——本机累计到 6809 个目录 / 17079 个文件 / 8.22 GB 就是这么来的。
///
/// 实测这些句柄在测试进程存活期间一直被持有（自适应等待 2 秒也删不掉），只有 testhost 退出后
/// 才释放。所以兜底分两层：
/// 1. <see cref="FlushAtExit"/>：进程退出时先清空全进程连接池再删，能删掉一部分；
/// 2. 跨进程清单：仍删不掉的路径写进 %TEMP% 下的清单文件，**下一次跑测试时**在
///    <see cref="DrainPendingFromPreviousRun"/> 里删除——那时上一个 testhost 已经退出，
///    句柄必然释放，删除一定成功。这样残留不会再逐次累积。
/// </summary>
internal static class TempCleanup
{
    private static readonly ConcurrentDictionary<string, byte> PendingDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> PendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private static int _hooked;

    internal static readonly string PendingListPath =
        Path.Combine(Path.GetTempPath(), "scada-test-cleanup-pending.txt");

    private static readonly object ListGate = new();

    public static void Track(string directory)
    {
        if (PendingDirectories.TryAdd(directory, 0)) AppendToPendingList(directory);
        Hook();
    }

    public static void TrackFile(string path)
    {
        if (PendingFiles.TryAdd(path, 0)) AppendToPendingList(path);
        Hook();
    }

    /// <summary>
    /// 登记即落盘。不能等到 ProcessExit 再写：VSTest 宿主退出时不保证触发
    /// <see cref="AppDomain.ProcessExit"/>（实测多条路径都不触发），依赖它会让清单永远为空。
    /// 每轮全量最多几十次追加，开销可以忽略。
    /// </summary>
    private static void AppendToPendingList(string path)
    {
        try
        {
            lock (ListGate)
                File.AppendAllText(PendingListPath, path + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>当前待清理的数量，供回归用例断言。</summary>
    public static int PendingCount => PendingDirectories.Count + PendingFiles.Count;

    private static void Hook()
    {
        if (Interlocked.Exchange(ref _hooked, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAtExit();
    }

    /// <summary>
    /// 进程退出专用：先清空全进程连接池，再删登记项，最后把仍删不掉的路径落盘留给下次启动。
    ///
    /// ProcessExit 触发时托管对象还没被回收，池里的 SqliteConnection 仍然握着文件句柄——
    /// 只删文件同样会失败。此处所有夹具都已结束，没有并发读者，清空全进程池是安全的；
    /// 夹具运行期绝不能这么做（会把并行夹具的连接一起打掉，见 TestSqlite 的类型注释）。
    /// </summary>
    public static void FlushAtExit()
    {
        try
        {
            // 部分测试项目不引用 Microsoft.Data.Sqlite，用反射清池，避免为挂钩清理而增加程序集依赖。
            var type = Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite");
            type?.GetMethod("ClearAllPools")?.Invoke(null, null);
        }
        catch (Exception) { /* 退出路径上不抛 */ }
        Flush();
        // 不再需要「把剩余路径落盘」：Track 时已经逐条追加过了，这里只管尽力删。
    }

    /// <summary>
    /// 清理上一次运行遗留的目录。由测试程序集的静态构造函数调用：
    /// 此时上一个 testhost 已退出，文件句柄全部释放，删除必然成功。
    /// </summary>
    public static void DrainPendingFromPreviousRun()
    {
        try
        {
            if (!File.Exists(PendingListPath)) return;
            var stillThere = new List<string>();
            lock (ListGate)
            {
                foreach (var line in File.ReadAllLines(PendingListPath))
                {
                    var path = line.Trim();
                    if (path.Length == 0) continue;
                    try { if (Directory.Exists(path)) Directory.Delete(path, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (Directory.Exists(path) || File.Exists(path)) stillThere.Add(path);
                }

                // 删干净的项从清单里去掉；仍然失败的留到下一次。清单不能只增不减。
                if (stillThere.Count == 0) File.Delete(PendingListPath);
                else File.WriteAllLines(PendingListPath, stillThere);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>尽力清理所有登记项；失败的项留在登记表里，不抛异常。</summary>
    public static void Flush()
    {
        foreach (var directory in PendingDirectories.Keys)
        {
            try
            {
                if (!Directory.Exists(directory)) { PendingDirectories.TryRemove(directory, out _); continue; }
                Directory.Delete(directory, true);
                PendingDirectories.TryRemove(directory, out _);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var path in PendingFiles.Keys)
        {
            try
            {
                foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                    if (File.Exists(file)) File.Delete(file);
                PendingFiles.TryRemove(path, out _);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

}

/// <summary>
/// 测试程序集加载时先清掉上一次运行遗留的临时目录。
/// 用 ModuleInitializer 而不是静态构造函数：它在任何测试类被触碰之前执行，
/// 且不受某个具体类型是否被引用影响。
/// </summary>
internal static class TestAssemblyBootstrap
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize() => TempCleanup.DrainPendingFromPreviousRun();
}
