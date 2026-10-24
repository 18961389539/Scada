using System.Collections.Concurrent;
using Scada.Core.Models;

namespace Scada.Server.Services;

// 登录失败限速：同一（用户名 + 来源地址）在窗口期内累计失败到上限后锁定一段时间。
// 计数只存内存，重启即清空——它防的是在线爆破，不承担跨重启的封禁持久化。
public sealed class LoginThrottle(Func<long>? clock = null)
{
    public const int MaxFailures = 5;
    public const long WindowMs = 5 * 60_000;
    public const long LockMs = 5 * 60_000;
    private const int MaxEntries = 10_000;

    private readonly Func<long> _now = clock ?? ScadaTime.NowMs;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public int Failures;
        public long FirstFailureTs;
        public long LockedUntilTs;
    }

    public int Count => _entries.Count;

    public static string Key(string userName, string? clientAddress) =>
        $"{userName.Trim().ToLowerInvariant()}|{(string.IsNullOrWhiteSpace(clientAddress) ? "-" : clientAddress)}";

    public bool IsLocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_entries.TryGetValue(key, out var entry)) return false;
        lock (entry)
        {
            var remaining = entry.LockedUntilTs - _now();
            if (remaining <= 0) return false;
            retryAfter = TimeSpan.FromMilliseconds(remaining);
            return true;
        }
    }

    public void RecordFailure(string key)
    {
        var now = _now();
        Prune(now);
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            if (entry.LockedUntilTs > now) return;
            if (entry.FirstFailureTs == 0 || now - entry.FirstFailureTs > WindowMs)
            {
                entry.FirstFailureTs = now;
                entry.Failures = 0;
            }

            entry.Failures++;
            if (entry.Failures >= MaxFailures)
            {
                entry.LockedUntilTs = now + LockMs;
                entry.Failures = 0;
                entry.FirstFailureTs = 0;
            }
        }
    }

    public void Reset(string key) => _entries.TryRemove(key, out _);

    private void Prune(long now)
    {
        if (_entries.Count < MaxEntries) return;
        foreach (var (key, entry) in _entries)
        {
            lock (entry)
            {
                if (entry.LockedUntilTs > now) continue;
                if (entry.FirstFailureTs != 0 && now - entry.FirstFailureTs <= WindowMs) continue;
                _entries.TryRemove(key, out _);
            }
        }
    }
}
