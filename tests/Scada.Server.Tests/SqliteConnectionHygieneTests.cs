using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Server.Tests;

// 迁移用 ATTACH DATABASE runtime 搬迁运行数据，而 SQLite 不允许在事务里 DETACH，
// 所以「配置库连接上还挂着 runtime」的物理连接会回到连接池，被之后的配置库上下文借走。
// 之后这条连接上的 BEGIN IMMEDIATE 会连同 runtime.db 一起上锁：运行库自己的写入
// （审计、报警、命令留痕）变成自己锁自己，一直等到 30 秒命令超时报 database is locked。
// 现场表现是「保存画面/工程时偶发 30 秒卡死」，测试里是 MCP 画面工具随机 30 秒超时。
public sealed class SqliteConnectionHygieneTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;

    public SqliteConnectionHygieneTests(AuthorizationTests.Site site) => _site = site;

    [Fact]
    public async Task ConfigConnections_Do_Not_Carry_Attached_Runtime_Database()
    {
        var factory = _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        // 同时占用多条上下文，逼连接池把不同的物理连接都发出来：
        // 一次只借一条的话，池会反复把同一条（恰好没挂载的）连接还给你。
        var contexts = new List<ConfigDbContext>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var db = await factory.CreateDbContextAsync();
                await db.Database.OpenConnectionAsync();
                contexts.Add(db);
            }

            foreach (var db in contexts)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA database_list";
                var attached = new List<string>();
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) attached.Add(reader.GetString(1));
                Assert.DoesNotContain("runtime", attached);
            }
        }
        finally
        {
            foreach (var db in contexts) await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConfigTransaction_Does_Not_Block_Concurrent_Runtime_Write()
    {
        var configs = _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        var runtimes = _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();

        await using var config = await configs.CreateDbContextAsync();
        await using var transaction = await config.Database.BeginTransactionAsync();
        // 事务里真的写一次配置库：BEGIN IMMEDIATE 只在有写操作时才会连带锁住挂载库。
        await config.Database.ExecuteSqlRawAsync("UPDATE \"Area\" SET \"Description\" = \"Description\" WHERE 0");

        await using var runtime = await runtimes.CreateDbContextAsync();
        runtime.OperationLogs.Add(new OperationLog
        {
            Ts = ScadaTime.NowMs(), Action = "并发写入探针", Result = "成功"
        });

        // 修复前：配置库事务连带锁住运行库，这里会一直等到 30 秒命令超时才失败。
        await runtime.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await transaction.RollbackAsync();
    }
}
