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

        var configConnection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(settings.ConfigDbPath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
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
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }
}
