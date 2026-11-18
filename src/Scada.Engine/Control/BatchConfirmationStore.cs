using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Scada.Core.Models;

namespace Scada.Engine.Control;

public sealed record BatchConfirmation(string Challenge, long ExpiresTs);

public sealed class BatchConfirmationStore
{
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromSeconds(60);

    private readonly record struct Key(int UserId, string Kind, int ResourceId, string Fingerprint);
    private sealed record Entry(byte[] Hash, long IssuedTs);

    private readonly ConcurrentDictionary<Key, Entry> _grants = new();
    private long _lastPurgeTs;

    public BatchConfirmation Grant(int? userId, string kind, int resourceId, string fingerprint)
    {
        if (userId is not int id) throw new InvalidOperationException("批量确认需要登录用户");
        Purge();
        var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = ScadaTime.NowMs();
        _grants[new Key(id, kind, resourceId, fingerprint)] = new Entry(SHA256.HashData(Encoding.UTF8.GetBytes(challenge)), now);
        return new(challenge, now + (long)Lifetime.TotalMilliseconds);
    }

    public bool TryConsume(int? userId, string kind, int resourceId, string fingerprint, string? challenge)
    {
        if (userId is not int id || string.IsNullOrWhiteSpace(challenge)) return false;
        if (!_grants.TryRemove(new Key(id, kind, resourceId, fingerprint), out var entry)) return false;
        if (ScadaTime.NowMs() - entry.IssuedTs > (long)Lifetime.TotalMilliseconds) return false;
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(challenge));
        return CryptographicOperations.FixedTimeEquals(actual, entry.Hash);
    }

    public static string Fingerprint(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts))));

    private void Purge()
    {
        var now = ScadaTime.NowMs();
        if (now - Interlocked.Read(ref _lastPurgeTs) < (long)Lifetime.TotalMilliseconds) return;
        Interlocked.Exchange(ref _lastPurgeTs, now);
        var cutoff = now - (long)Lifetime.TotalMilliseconds;
        foreach (var pair in _grants)
            if (pair.Value.IssuedTs < cutoff) _grants.TryRemove(pair.Key, out _);
    }
}