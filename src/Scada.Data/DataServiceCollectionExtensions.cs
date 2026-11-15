using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data.Auditing;
using Scada.Data.Infrastructure;

namespace Scada.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddScadaData(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ScadaSettings>(configuration.GetSection("Scada"));
        var settings = configuration.GetSection("Scada").Get<ScadaSettings>() ?? new ScadaSettings();

        // 缓存模式保持默认的 private：shared cache 与 WAL 同时启用会退化成表级锁，
        // 且连接池复用时跨 DbContext 的可见性不再等同于 READ COMMITTED（会读到他事务未提交的行）。
        // 配置库是单写多读，私有缓存 + WAL 才是 SQLite 的推荐组合。
        var configConnection = ConfigConnectionString(settings);
        var runtimeConnection = RuntimeConnectionString(settings);
        // 迁移阶段的搬迁目标必须与上面这条连接指向同一个文件，否则升级会把运行数据搬进另一个库。
        OperationalDataMover.RegisterRuntimePath(settings.ConfigDbPath, ResolveRuntimePath(settings));

        services.AddDbContextFactory<ConfigDbContext>((sp, options) =>
            options.UseSqlite(configConnection)
                // 配置与运行数据用 FULL：组态、报警、命令回执与审计不能因掉电回滚已提交事务。
                // 历史库走自己的 NORMAL（见 HistoryDbFactory / Scada:HistorySynchronous）。
                .AddInterceptors(new SqlitePragmaInterceptor(fullDurability: true), new LicenseQuotaInterceptor(sp), new AuditInterceptor(sp)));
        services.AddDbContextFactory<RuntimeDbContext>(options =>
            options.UseSqlite(runtimeConnection).AddInterceptors(new SqlitePragmaInterceptor(fullDurability: true)));

        services.AddSingleton<IHistoryDbFactory, HistoryDbFactory>();
        services.AddSingleton<IConfigSnapshot, ConfigSnapshot>();
        services.AddSingleton<AccessModelProvider>();
        // 启动迁移的条数/耗时快照；DatabaseInitializer 写入，/metrics 读取。
        services.AddSingleton<MigrationTelemetry>();
        // 审计发件箱的投递器：拦截器内联调用它（短超时），后台服务按周期补齐没投成功的行。
        services.AddSingleton<AuditOutboxDrainer>();
        services.AddHostedService<AuditOutboxService>();
        // 跨进程配置刷新：别的宿主改了配置，本进程也能在 ConfigRefreshSeconds 内看到。
        services.AddHostedService<ConfigRefreshService>();
        return services;
    }

    public static string ResolveRuntimePath(ScadaSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.RuntimeDbPath)) return Path.GetFullPath(settings.RuntimeDbPath);
        return OperationalDataMover.SiblingPath(Path.GetFullPath(settings.ConfigDbPath))
            ?? Path.Combine(AppContext.BaseDirectory, "runtime.db");
    }

    /// <summary>
    /// 配置库连接串。<paramref name="pooling"/> 传 false 时用于一次性连接（例如迁移）：
    /// 迁移会用 <c>ATTACH DATABASE runtime</c> 搬迁运行数据，而 SQLite 不允许在事务里 DETACH，
    /// 这条物理连接若进了连接池，之后任何配置库事务的 <c>BEGIN IMMEDIATE</c> 都会连带锁住 runtime.db，
    /// 运行库自己的写入（审计、报警、命令留痕）就变成自己锁自己，一直等到 30 秒命令超时报 database is locked。
    /// </summary>
    public static string ConfigConnectionString(ScadaSettings settings, bool pooling = true) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(settings.ConfigDbPath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = pooling
        }.ToString();

    /// <summary>运行库连接串（与 DI 注册的那个字符串完全一致，用于按库清池）。</summary>
    public static string RuntimeConnectionString(ScadaSettings settings)
    {
        var path = ResolveRuntimePath(settings);
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }
}
