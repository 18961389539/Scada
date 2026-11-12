using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Scripting;
using Scada.Data;

namespace Scada.Engine.Calculations;

public sealed record SerialProfileSnapshot(
    IReadOnlyDictionary<string, SerialProfile> Profiles,
    IReadOnlyDictionary<string, string> Sources,
    int DatabaseCount);

// 串口配置的唯一读取入口：数据库覆盖同名 appsettings 配置，缓存几秒并在配置保存后失效。
public interface ISerialProfileCatalog
{
    Task<SerialProfileSnapshot> GetAsync(CancellationToken ct = default);
    void Invalidate();
}

public sealed class SerialProfileCatalog(
    IDbContextFactory<ConfigDbContext> factory,
    IOptions<CSharpScriptOptions> options,
    ILogger<SerialProfileCatalog> logger) : ISerialProfileCatalog
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SerialProfileSnapshot? _snapshot;
    private DateTimeOffset _expires;
    private long _invalidation;
    private long _loadedInvalidation;

    public void Invalidate() => Interlocked.Increment(ref _invalidation);

    public async Task<SerialProfileSnapshot> GetAsync(CancellationToken ct = default)
    {
        var invalidation = Interlocked.Read(ref _invalidation);
        var snapshot = _snapshot;
        if (snapshot is not null && invalidation == _loadedInvalidation && DateTimeOffset.UtcNow < _expires) return snapshot;

        await _gate.WaitAsync(ct);
        try
        {
            invalidation = Interlocked.Read(ref _invalidation);
            snapshot = _snapshot;
            if (snapshot is not null && invalidation == _loadedInvalidation && DateTimeOffset.UtcNow < _expires) return snapshot;

            var profiles = new Dictionary<string, SerialProfile>(StringComparer.OrdinalIgnoreCase);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, profile) in options.Value.SerialProfiles ?? new())
            {
                if (SerialProfileRules.Validate(name, profile) is { } error)
                {
                    logger.LogWarning("Ignoring serial profile {Name} from configuration: {Error}", name, error);
                    continue;
                }
                profiles[name.Trim()] = profile;
                sources[name.Trim()] = "config";
            }

            var databaseCount = 0;
            try
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                var rows = await db.SerialProfiles.AsNoTracking().Where(row => row.Enabled).OrderBy(row => row.Id).ToListAsync(ct);
                databaseCount = rows.Count;
                foreach (var row in rows)
                {
                    var name = row.Name?.Trim() ?? "";
                    var profile = row.CloneProfile();
                    if (SerialProfileRules.Validate(name, profile) is { } error)
                    {
                        logger.LogWarning("Ignoring serial profile {Name} from database: {Error}", name, error);
                        continue;
                    }
                    profiles[name] = profile;
                    sources[name] = "database";
                }
            }
            catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Cannot load serial profiles from the config database; using configuration profiles only.");
            }

            snapshot = new SerialProfileSnapshot(profiles, sources, databaseCount);
            _snapshot = snapshot;
            _loadedInvalidation = invalidation;
            _expires = DateTimeOffset.UtcNow.Add(Ttl);
            return snapshot;
        }
        finally { _gate.Release(); }
    }
}
