using System.Collections.Concurrent;

namespace Scada.Server.Services;

// 已建立实时连接的登记表：HTTP 令牌在每次请求都会被重新校验，
// 但长连接的声明在建立时就被固定，必须由这里主动断开才能真正撤销。
public sealed class SessionRegistry
{
    public sealed record Entry(int UserId, string Role, string CredentialsVersion, Action Abort);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    public void Register(string connectionId, int userId, string? role, string? credentialsVersion, Action abort) =>
        _sessions[connectionId] = new Entry(userId, role ?? string.Empty, credentialsVersion ?? string.Empty, abort);

    public int? UserOf(string connectionId) => _sessions.TryGetValue(connectionId, out var entry) ? entry.UserId : null;

    public IReadOnlyList<(string ConnectionId, int UserId)> Connections() => _sessions.Select(x => (x.Key, x.Value.UserId)).ToList();

    public void Unregister(string connectionId) => _sessions.TryRemove(connectionId, out _);

    public int RevokeUser(int userId) => AbortWhere(entry => entry.UserId == userId);

    public int AbortWhere(Func<Entry, bool> shouldAbort)
    {
        var aborted = 0;
        foreach (var (connectionId, entry) in _sessions)
        {
            bool revoke;
            try { revoke = shouldAbort(entry); }
            catch { revoke = false; }
            if (!revoke || !_sessions.TryRemove(connectionId, out var removed)) continue;
            aborted++;
            try { removed.Abort(); } catch { }
        }

        return aborted;
    }
}
