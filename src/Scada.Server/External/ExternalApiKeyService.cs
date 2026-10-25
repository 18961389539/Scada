using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Microsoft.Extensions.Options;

namespace Scada.Server.External;

public sealed record ExternalApiKeyView(int Id, string Name, string KeyPrefix, string[] Scopes, int[]? AreaIds,
    int RateLimitPerMinute, bool Enabled, long CreatedTs, string? CreatedBy, long? LastUsedTs, long? RevokedTs, string? Notes);

public sealed record ExternalApiKeyCreated(ExternalApiKeyView Key, string Secret);

public sealed class ExternalApiKeyService(
    IDbContextFactory<ConfigDbContext> factory,
    AccessModelProvider access,
    IOptionsMonitor<ExternalIntegrationOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, (ExternalApiKey Key, long Loaded)> _byHash = new();
    private readonly ConcurrentDictionary<int, RateBucket> _rates = new();

    public static string Hash(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes);
    }

    public static string GenerateSecret(out string prefix)
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        prefix = "scada_" + raw[..8];
        return prefix + raw[8..];
    }

    public async Task<IReadOnlyList<ExternalApiKeyView>> ListAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.ExternalApiKeys.AsNoTracking().OrderByDescending(x => x.Id).ToListAsync(ct)).Select(View).ToList();
    }

    public async Task<ExternalApiKeyCreated> CreateAsync(string name, ExternalApiScope scopes, int[]? areaIds, int? rateLimit, string? notes, string createdBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) throw new ArgumentException("名称须为 1～100 字");
        if (scopes == ExternalApiScope.None) throw new ArgumentException("至少选择一个权限范围");
        if (areaIds is { Length: > 0 } && areaIds.Any(a => a <= 0)) throw new ArgumentException("区域编号无效");
        var secret = GenerateSecret(out var prefix);
        var key = new ExternalApiKey
        {
            Name = name.Trim(),
            KeyPrefix = prefix,
            KeyHash = Hash(secret),
            Scopes = scopes,
            AreaIdsJson = areaIds is { Length: > 0 } ? JsonSerializer.Serialize(areaIds.Distinct().OrderBy(x => x).ToArray()) : null,
            RateLimitPerMinute = Math.Clamp(rateLimit ?? options.CurrentValue.Rest.DefaultRateLimitPerMinute, 1, 100_000),
            Enabled = true,
            CreatedTs = ScadaTime.NowMs(),
            CreatedBy = createdBy,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()[..Math.Min(notes.Trim().Length, 500)]
        };
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.ExternalApiKeys.AnyAsync(x => x.Name == key.Name && x.RevokedTs == null, ct))
            throw new ArgumentException("同名密钥已存在");
        db.ExternalApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        _byHash[key.KeyHash] = (key, ScadaTime.NowMs());
        return new(View(key), secret);
    }

    public async Task<ExternalApiKeyView?> RevokeAsync(int id, string by, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = await db.ExternalApiKeys.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (key is null) return null;
        if (key.RevokedTs is null)
        {
            key.RevokedTs = ScadaTime.NowMs();
            key.Enabled = false;
            key.Notes = string.IsNullOrEmpty(key.Notes) ? $"revoked by {by}" : key.Notes + $" · revoked by {by}";
            await db.SaveChangesAsync(ct);
            _byHash.TryRemove(key.KeyHash, out _);
        }
        return View(key);
    }

    public async Task<ExternalApiKeyView?> SetEnabledAsync(int id, bool enabled, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = await db.ExternalApiKeys.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (key is null || key.RevokedTs is not null) return null;
        key.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        _byHash[key.KeyHash] = (Clone(key), ScadaTime.NowMs());
        return View(key);
    }

    public async Task<ExternalApiKey?> FindBySecretAsync(string secret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith("scada_", StringComparison.Ordinal)) return null;
        var hash = Hash(secret.Trim());
        if (_byHash.TryGetValue(hash, out var cached) && ScadaTime.NowMs() - cached.Loaded < 30_000)
            return cached.Key.Enabled && cached.Key.RevokedTs is null ? cached.Key : null;
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = await db.ExternalApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.KeyHash == hash, ct);
        if (key is null) return null;
        _byHash[hash] = (Clone(key), ScadaTime.NowMs());
        return key.Enabled && key.RevokedTs is null ? key : null;
    }

    public async Task TouchAsync(int id, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await db.ExternalApiKeys.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedTs, ScadaTime.NowMs()), ct);
        }
        catch { /* 触摸失败不影响请求 */ }
    }

    public bool TryAcquire(ExternalApiKey key, out int retryAfterSec)
    {
        var limit = Math.Max(1, key.RateLimitPerMinute);
        var bucket = _rates.GetOrAdd(key.Id, _ => new RateBucket());
        return bucket.TryAcquire(limit, out retryAfterSec);
    }

    public async Task<AccessContext> AccessForKeyAsync(ExternalApiKey key, CancellationToken ct)
    {
        var model = await access.GetAsync(ct);
        var perms = Permission.None;
        if (key.Scopes.HasFlag(ExternalApiScope.ReadTags) || key.Scopes.HasFlag(ExternalApiScope.ReadHistory) || key.Scopes.HasFlag(ExternalApiScope.ReadAlarms))
            perms |= Permission.View;
        if (key.Scopes.HasFlag(ExternalApiScope.Write))
            perms |= Permission.Control;
        var areas = ParseAreas(key.AreaIdsJson);
        var grants = areas is null
            ? new List<AccessGrant> { new(perms, null) }
            : areas.Select(a => new AccessGrant(perms, a)).ToList();
        return new AccessContext(null, grants, model);
    }

    public static ExternalApiScope ScopesFromUser(AccessContext access)
    {
        var scopes = ExternalApiScope.None;
        if (access.HasAny(Permission.View)) scopes |= ExternalApiScope.ReadTags | ExternalApiScope.ReadHistory | ExternalApiScope.ReadAlarms;
        if (access.HasAny(Permission.Control)) scopes |= ExternalApiScope.Write;
        return scopes;
    }

    public static bool HasScope(ExternalApiScope have, ExternalApiScope need) => (have & need) == need;

    public static int[]? ParseAreas(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var ids = JsonSerializer.Deserialize<int[]>(json, Json);
            return ids is { Length: > 0 } ? ids : null;
        }
        catch { return null; }
    }

    public static ExternalApiKeyView View(ExternalApiKey k) => new(
        k.Id, k.Name, k.KeyPrefix,
        Enum.GetValues<ExternalApiScope>().Where(s => s != ExternalApiScope.None && s != ExternalApiScope.All && k.Scopes.HasFlag(s)).Select(s => s.ToString()).ToArray(),
        ParseAreas(k.AreaIdsJson), k.RateLimitPerMinute, k.Enabled, k.CreatedTs, k.CreatedBy, k.LastUsedTs, k.RevokedTs, k.Notes);

    private static ExternalApiKey Clone(ExternalApiKey k) => new()
    {
        Id = k.Id, Name = k.Name, KeyPrefix = k.KeyPrefix, KeyHash = k.KeyHash, Scopes = k.Scopes,
        AreaIdsJson = k.AreaIdsJson, RateLimitPerMinute = k.RateLimitPerMinute, Enabled = k.Enabled,
        CreatedTs = k.CreatedTs, CreatedBy = k.CreatedBy, LastUsedTs = k.LastUsedTs, RevokedTs = k.RevokedTs, Notes = k.Notes
    };

    private sealed class RateBucket
    {
        private readonly Queue<long> _hits = new();
        private readonly object _gate = new();
        public bool TryAcquire(int limitPerMinute, out int retryAfterSec)
        {
            lock (_gate)
            {
                var now = ScadaTime.NowMs();
                var window = now - 60_000;
                while (_hits.Count > 0 && _hits.Peek() < window) _hits.Dequeue();
                if (_hits.Count >= limitPerMinute)
                {
                    retryAfterSec = Math.Max(1, (int)Math.Ceiling((_hits.Peek() + 60_000 - now) / 1000.0));
                    return false;
                }
                _hits.Enqueue(now);
                retryAfterSec = 0;
                return true;
            }
        }
    }
}
