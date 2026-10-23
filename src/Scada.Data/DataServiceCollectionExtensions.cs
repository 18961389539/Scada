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

        services.AddDbContextFactory<ConfigDbContext>(options =>
            options.UseSqlite(configConnection)
                .AddInterceptors(new SqlitePragmaInterceptor()));

        services.AddSingleton<IHistoryDbFactory, HistoryDbFactory>();
        services.AddSingleton<IConfigSnapshot, ConfigSnapshot>();
        return services;
    }
}
