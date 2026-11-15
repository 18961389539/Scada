using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data.Auditing;
using Scada.Data.Infrastructure;

namespace Scada.Data.Tests;

/// <summary>
/// 审计必须与配置写入同事务落库：进程崩溃、运行库被锁都不能让"配置改了但没痕迹"发生。
/// 这里同时锁定三件事：发件箱行随业务提交、创建后主键能回填、运行库不可用时业务写入仍然成功。
/// </summary>
public sealed class AuditOutboxTests
{
    [Fact]
    public async Task CreateWritesOneAuditRowWithTheGeneratedId()
    {
        using var root = new TempRoot();
        await using var provider = Build(root.Path("scada.db"), root.Path("runtime.db"), migrateRuntime: true);

        var deviceId = await CreateDeviceAsync(provider, "审计发件箱设备");

        await using var runtime = await RuntimeAsync(provider);
        var log = await runtime.OperationLogs.AsNoTracking().SingleAsync();
        Assert.Equal("创建设备：审计发件箱设备", log.Action);
        Assert.Equal("Device", log.TargetType);
        // 主键是 INSERT 之后才有的：发件箱行必须被回填，否则前端"查看对象"点不过去。
        Assert.Equal(deviceId.ToString(), log.TargetId);
        Assert.Contains("\"op\":\"create\"", log.Details);

        await using var config = await ConfigAsync(provider);
        // 内联投递刻意不删发件箱行（删行要抢配置库写锁，端点事务未提交时会卡到命令超时）。
        Assert.Equal(1, await config.AuditOutbox.CountAsync());
        // 后台补跑一轮：按 SourceId 查重后安全删行，不会写出重复审计。
        Assert.True(await provider.GetRequiredService<AuditOutboxDrainer>().DrainAsync(10, 2, CancellationToken.None));
        Assert.Equal(0, await config.AuditOutbox.CountAsync());
        Assert.Equal(1, await runtime.OperationLogs.CountAsync());
    }

    [Fact]
    public async Task UpdateWritesDiffAgainstThePersistedValues()
    {
        using var root = new TempRoot();
        await using var provider = Build(root.Path("scada.db"), root.Path("runtime.db"), migrateRuntime: true);
        var deviceId = await CreateDeviceAsync(provider, "待改设备");

        await using (var config = await ConfigAsync(provider))
        {
            var device = await config.Devices.SingleAsync(x => x.Id == deviceId);
            device.PollIntervalMs = 2500;
            await config.SaveChangesAsync();
        }

        await using var runtime = await RuntimeAsync(provider);
        var update = await runtime.OperationLogs.AsNoTracking().SingleAsync(x => x.Action == "修改设备：待改设备");
        Assert.Contains("\"pollIntervalMs\"", update.Details);
        Assert.Contains("\"op\":\"update\"", update.Details);
        Assert.Equal(deviceId.ToString(), update.TargetId);
    }

    [Fact]
    public async Task ConfigSaveSurvivesAnUnreachableRuntimeDatabase()
    {
        using var root = new TempRoot();
        // 用一个普通文件当父目录：SQLite 打不开 runtime.db，模拟运行库不可用/被锁死。
        File.WriteAllText(root.Path("blocked"), "not a directory");
        var blocked = System.IO.Path.Combine(root.Root, "blocked", "runtime.db");
        await using var provider = Build(root.Path("scada.db"), blocked, migrateRuntime: false);

        var deviceId = await CreateDeviceAsync(provider, "运行库不可用");

        Assert.True(deviceId > 0);
        await using var config = await ConfigAsync(provider);
        var pending = await config.AuditOutbox.AsNoTracking().SingleAsync();
        Assert.Contains("运行库不可用", pending.Payload);
        // 内联投递失败时连退避都不写：写退避要抢配置库写锁，会把业务保存拖到命令超时。
        // 退避由后台服务负责，这里只保证行没丢、还在发件箱里。
        Assert.Equal(0, pending.Attempts);
    }

    [Fact]
    public async Task SaveInsideAnExplicitTransactionDoesNotWaitForTheOutbox()
    {
        using var root = new TempRoot();
        await using var provider = Build(root.Path("scada.db"), root.Path("runtime.db"), migrateRuntime: true);
        await using var config = await ConfigAsync(provider);
        await using var transaction = await config.Database.BeginTransactionAsync();
        config.Devices.Add(new Device { Name = "事务内保存", Protocol = ProtocolType.ModbusTcp, Host = "127.0.0.1" });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await config.SaveChangesAsync();
        watch.Stop();

        // 拦截器是在 SaveChangesAsync 里做内联投递的，此时端点事务还没提交。
        // 只要内联路径去写配置库（删发件箱行或写退避），就会等这把写锁到命令超时，
        // 表现成集成请求 30 秒超时。修复后内联只读配置库、只写运行库。
        Assert.True(watch.Elapsed.TotalSeconds < 3, $"配置保存被审计投递拖住 {watch.Elapsed.TotalSeconds:0.0} 秒");
        Assert.Single(await config.AuditOutbox.AsNoTracking().ToListAsync());
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task RedeliveryOfTheSameOutboxRowDoesNotDuplicateTheAudit()
    {
        using var root = new TempRoot();
        await using var provider = Build(root.Path("scada.db"), root.Path("runtime.db"), migrateRuntime: true);
        var drainer = provider.GetRequiredService<AuditOutboxDrainer>();
        var payload = System.Text.Json.JsonSerializer.Serialize(new OperationLog
        {
            UserName = "system", Ts = 1, Action = "测试：重复投递", Result = "成功", TargetType = "Device", TargetId = "7"
        });

        await InsertOutboxAsync(provider, 42, payload);
        Assert.True(await drainer.DrainAsync(10, 2, CancellationToken.None));

        // 模拟"投递成功但删行前崩溃"：同一行（同 Id）重新出现。
        await InsertOutboxAsync(provider, 42, payload);
        Assert.True(await drainer.DrainAsync(10, 2, CancellationToken.None));

        await using var runtime = await RuntimeAsync(provider);
        Assert.Equal(1, await runtime.OperationLogs.CountAsync(x => x.Action == "测试：重复投递"));
        await using var config = await ConfigAsync(provider);
        Assert.Equal(0, await config.AuditOutbox.CountAsync());
    }

    private static async Task InsertOutboxAsync(IServiceProvider provider, long id, string payload)
    {
        await using var config = await ConfigAsync(provider);
        await config.Database.ExecuteSqlAsync(
            $"INSERT INTO AuditOutbox (Id, CreatedTs, Payload, Attempts, NextAttemptTs) VALUES ({id}, 0, {payload}, 0, 0)");
    }

    private static ServiceProvider Build(string configPath, string runtimePath, bool migrateRuntime)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<ConfigDbContext>((sp, options) => options
            .UseSqlite($"Data Source={configPath};Pooling=False")
            .AddInterceptors(new SqlitePragmaInterceptor(fullDurability: true), new AuditInterceptor(sp)));
        services.AddDbContextFactory<RuntimeDbContext>(options => options
            .UseSqlite($"Data Source={runtimePath};Pooling=False")
            .AddInterceptors(new SqlitePragmaInterceptor(fullDurability: true)));
        services.AddSingleton<AuditOutboxDrainer>();
        var provider = services.BuildServiceProvider();

        using (var config = provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContext())
            config.Database.Migrate();
        if (migrateRuntime)
        {
            using var runtime = provider.GetRequiredService<IDbContextFactory<RuntimeDbContext>>().CreateDbContext();
            runtime.Database.Migrate();
        }
        return provider;
    }

    private static async Task<int> CreateDeviceAsync(IServiceProvider provider, string name)
    {
        await using var config = await ConfigAsync(provider);
        var device = new Device { Name = name, Protocol = ProtocolType.ModbusTcp, Host = "127.0.0.1" };
        config.Devices.Add(device);
        await config.SaveChangesAsync();
        return device.Id;
    }

    private static Task<ConfigDbContext> ConfigAsync(IServiceProvider provider) =>
        provider.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync();

    private static Task<RuntimeDbContext> RuntimeAsync(IServiceProvider provider) =>
        provider.GetRequiredService<IDbContextFactory<RuntimeDbContext>>().CreateDbContextAsync();
}
