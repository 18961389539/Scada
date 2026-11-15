using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data.Infrastructure;

namespace Scada.Data.Tests;

/// <summary>
/// 历史库按月分文件，月初会有多个查询同时打开同一个新月份。
/// 另一个易忽略的场景是只读查询进程（viewer / 只读副本）打开一个已存在的旧月份：
/// 旧实现会无条件跑 Migrate() 和一组 DDL，直接把只读连接打成 "attempt to write a readonly database"。
/// </summary>
public sealed class HistoryDbFactoryTests
{
    [Fact]
    public async Task ConcurrentCreateOfTheSameMonthBuildsOneSchema()
    {
        using var root = new TempRoot();
        var factory = Build(root.Path("history"));

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = await factory.CreateRawAsync(2026, 12);
            return await db.Database.ExecuteSqlRawAsync("SELECT 1");
        })).ToArray();
        await Task.WhenAll(tasks);

        var path = factory.GetRawPath(2026, 12);
        Assert.True(File.Exists(path));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly
        }.ConnectionString);
        connection.Open();
        Assert.True(TempRoot.Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='HistoryValue'") > 0);
    }

    [Fact]
    public async Task OpeningAnUpToDateFileOnAQueryOnlyConnectionPerformsNoWrites()
    {
        using var root = new TempRoot();
        var writer = Build(root.Path("history"));
        await using (var db = await writer.CreateRawAsync(2026, 11)) { }
        var path = writer.GetRawPath(2026, 11);
        SqliteConnection.ClearAllPools();

        // 全新的 factory：_initialized 为空，等价于另一个进程首次打开这个旧月份。
        var reader = Build(root.Path("history"));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ConnectionString);
        connection.Open();
        TempRoot.Exec(connection, "PRAGMA query_only=ON");
        await using var readOnlyDb = new HistoryDbContext(
            new DbContextOptionsBuilder<HistoryDbContext>().UseSqlite(connection).Options);

        // 任何 CREATE/DROP/INSERT 都会在这里抛异常，这正是本用例要守住的行为。
        reader.EnsureCreated(readOnlyDb);
        Assert.True(TempRoot.Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='Agg5Min_QueueAggregation'") > 0);
    }

    [Fact]
    public void ReleaseFileClearsThePerFileGateAndInitializationFlag()
    {
        using var root = new TempRoot();
        var factory = Build(root.Path("history"));
        var path = factory.GetRawPath(2026, 10);
        factory.EnsureCreated(new HistoryDbContext(
            new DbContextOptionsBuilder<HistoryDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())
                .Options));
        factory.ReleaseFile(path);
        // 重新初始化同一个文件必须能再次完整走一遍，不能因为残留的 initialized 标记跳过建表。
        factory.EnsureCreated(new HistoryDbContext(
            new DbContextOptionsBuilder<HistoryDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString())
                .Options));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly
        }.ConnectionString);
        connection.Open();
        Assert.True(TempRoot.Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='HistoryValue'") > 0);
    }

    private static HistoryDbFactory Build(string root) =>
        new(Options.Create(new ScadaSettings { HistoryRoot = root }));
}
