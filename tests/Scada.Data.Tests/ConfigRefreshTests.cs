using Microsoft.Data.Sqlite;

namespace Scada.Data.Tests;

/// <summary>
/// 跨进程配置刷新靠 <c>PRAGMA data_version</c>：它只在别的连接提交后变化，自己的写入不改变它，
/// 因此轮询方可以长期持有一条只读语义的连接，不产生写锁。
/// </summary>
public sealed class ConfigRefreshTests
{
    [Fact]
    public void DataVersionChangesWhenAnotherConnectionCommits()
    {
        using var root = new TempRoot();
        using var watcher = root.Open("cfg.db");
        using var writer = root.Open("cfg.db");

        TempRoot.Exec(writer, "CREATE TABLE \"T\" (\"Id\" INTEGER NOT NULL PRIMARY KEY)");
        var before = ConfigRefreshService.DataVersion(watcher);

        TempRoot.Exec(writer, "INSERT INTO \"T\" DEFAULT VALUES");
        var afterWriter = ConfigRefreshService.DataVersion(watcher);
        Assert.NotEqual(before, afterWriter);

        // 自己的写入不改自己的 data_version：否则写进程会把每次保存都当成"别人改了"再重载一遍。
        TempRoot.Exec(watcher, "INSERT INTO \"T\" DEFAULT VALUES");
        Assert.Equal(afterWriter, ConfigRefreshService.DataVersion(watcher));
    }
}
