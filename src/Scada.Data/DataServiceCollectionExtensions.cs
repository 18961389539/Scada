using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Interfaces;
using Scada.Core.Options;
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
        var configConnection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(settings.ConfigDbPath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
        var runtimeConnection = RuntimeConnection(settings);

        services.AddDbContextFactory<ConfigDbContext>((sp, options) =>
            options.UseSqlite(configConnection)
                .AddInterceptors(new SqlitePragmaInterceptor(), new LicenseQuotaInterceptor(sp)));
        services.AddDbContextFactory<RuntimeDbContext>(options =>
            options.UseSqlite(runtimeConnection).AddInterceptors(new SqlitePragmaInterceptor()));

        services.AddSingleton<IHistoryDbFactory, HistoryDbFactory>();
        services.AddSingleton<IConfigSnapshot, ConfigSnapshot>();
        services.AddSingleton<AccessModelProvider>();
        return services;
    }

    public static string ResolveRuntimePath(ScadaSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.RuntimeDbPath)) return Path.GetFullPath(settings.RuntimeDbPath);
        return OperationalDataMover.SiblingPath(Path.GetFullPath(settings.ConfigDbPath))
            ?? Path.Combine(AppContext.BaseDirectory, "runtime.db");
    }

    private static string RuntimeConnection(ScadaSettings settings)
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
