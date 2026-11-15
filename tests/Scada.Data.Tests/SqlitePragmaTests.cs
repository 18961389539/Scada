using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Data.Infrastructure;

namespace Scada.Data.Tests;

/// <summary>
/// 外键此前完全依赖驱动默认值，但 schema 重建、运行表搬迁都建立在"外键是否生效"这一前提上。
/// 这里把 pragma 变成可断言的契约。
/// </summary>
public sealed class SqlitePragmaTests
{
    [Fact]
    public async Task ConfigConnectionsTurnOnForeignKeysAndWal()
    {
        using var root = new TempRoot();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ConfigDbContext>(options => options
            .UseSqlite($"Data Source={root.Path("scada.db")};Pooling=False")
            .AddInterceptors(new SqlitePragmaInterceptor(fullDurability: true)));
        await using var provider = services.BuildServiceProvider();
        await using var db = await provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();

        // 必须走 EF 的 OpenConnection：pragma 拦截器挂在 RelationalConnection 上，
        // 直接 db.Database.GetDbConnection().Open() 会绕过它，测出来的还是驱动默认值。
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys"));
        Assert.Equal("wal", Text(connection, "PRAGMA journal_mode"));
        Assert.Equal(5_000L, Scalar(connection, "PRAGMA busy_timeout"));
        Assert.Equal(-8_000L, Scalar(connection, "PRAGMA cache_size"));
        await db.Database.CloseConnectionAsync();
    }

    [Fact]
    public async Task HistoryConnectionsGetTheLargerCacheWithoutLosingWal()
    {
        using var root = new TempRoot();
        var services = new ServiceCollection();
        services.Configure<Scada.Core.Options.ScadaSettings>(x => x.HistoryRoot = root.Root);
        services.AddSingleton<IHistoryDbFactory, HistoryDbFactory>();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHistoryDbFactory>();

        await using var db = await factory.CreateRawAsync(2026, 11);
        await db.Database.OpenConnectionAsync();
        var connection = db.Database.GetDbConnection();
        Assert.Equal("wal", Text(connection, "PRAGMA journal_mode"));
        Assert.Equal(-32_000L, Scalar(connection, "PRAGMA cache_size"));
        await db.Database.CloseConnectionAsync();
    }

    [Fact]
    public async Task ConcurrentFirstOpenOfTheSameMonthCreatesOneFile()
    {
        using var root = new TempRoot();
        var services = new ServiceCollection();
        services.Configure<Scada.Core.Options.ScadaSettings>(x => x.HistoryRoot = root.Root);
        services.AddSingleton<IHistoryDbFactory, HistoryDbFactory>();
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHistoryDbFactory>();

        // 月初的第一个查询常常是并发的：两个请求同时跑 Migrate 会 database is locked 或
        // table already exists，只在月初偶发，所以必须用闸门串起来。
        var contexts = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => factory.CreateRawAsync(2026, 12))));

        foreach (var context in contexts) await context.DisposeAsync();
        var files = Directory.GetFiles(root.Root, "raw_202612.db*");
        Assert.Contains(files, path => path.EndsWith("raw_202612.db", StringComparison.OrdinalIgnoreCase));
    }

    private static long Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Text(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }
}
