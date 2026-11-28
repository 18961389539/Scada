using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Data;

namespace Scada.Server.Auth;

/// <summary>
/// 监视专用 Kiosk 播放列表的服务端会话。按登录令牌（jti，或旧令牌的哈希）绑定，
/// 不再用「最后一次进入」覆盖整个账号。随令牌过期；登录、注销、账号撤销时清理。
/// 会话同时写入运行库，主备复制后升主可重建，避免监视大屏在切换窗口里反而能下发。
/// </summary>
public sealed class KioskMonitorSessionStore
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly object _loadLock = new();
    private readonly IDbContextFactory<RuntimeDbContext>? _runtime;
    private readonly INodeRole _role;
    private readonly IRuntimeReplicationStatus _replication;
    private readonly TimeProvider _clock;
    private long _loadedGeneration = long.MinValue;
    private bool _loadSucceeded;

    public KioskMonitorSessionStore(
        IDbContextFactory<RuntimeDbContext>? runtime = null,
        INodeRole? role = null,
        TimeProvider? clock = null,
        IRuntimeReplicationStatus? replication = null)
    {
        _runtime = runtime;
        _role = role ?? new StandaloneNodeRole();
        _clock = clock ?? TimeProvider.System;
        _replication = replication ?? TrustedRuntimeReplication.Instance;
    }

    private readonly record struct Entry(int UserId, int PlaylistId, long ExpiresTs);

    public static string? SessionKey(HttpContext http)
    {
        var jti = http.User.GetSessionId();
        if (!string.IsNullOrWhiteSpace(jti)) return jti;
        var authorization = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && authorization.Length > prefix.Length)
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorization[prefix.Length..])));
        return null;
    }

    public void Enter(HttpContext http, int playlistId)
    {
        if (http.User.GetUserId() is not int userId) return;
        var key = SessionKey(http);
        if (string.IsNullOrWhiteSpace(key)) return;
        var expires = http.User.GetTokenExpiresTs() ?? Now() + 8 * 3600_000L;
        Upsert(key, userId, playlistId, expires);
    }

    public void Leave(HttpContext http)
    {
        var key = SessionKey(http);
        if (!string.IsNullOrWhiteSpace(key)) Remove(key);
    }

    public void LeaveUser(int userId)
    {
        foreach (var (key, entry) in _sessions)
            if (entry.UserId == userId) _sessions.TryRemove(key, out _);
        if (_runtime is null) return;
        try
        {
            using var db = _runtime.CreateDbContext();
            db.KioskMonitorSessions.Where(x => x.UserId == userId).ExecuteDelete();
        }
        catch
        {
            // 运行库尚未迁移或暂时不可用时，至少清掉内存里的锁。
        }
    }

    public void Transfer(string? fromSessionId, HttpContext http)
    {
        if (http.User.GetUserId() is not int userId) return;
        var to = SessionKey(http);
        if (string.IsNullOrWhiteSpace(to)) return;
        Entry? existing = null;
        if (!string.IsNullOrWhiteSpace(fromSessionId) && _sessions.TryRemove(fromSessionId, out var moved))
            existing = moved;
        // 内存命中后也必须删掉运行库旧行，否则升主/EnsureFresh 会把已迁移的旧令牌锁再加载回来。
        if (!string.IsNullOrWhiteSpace(fromSessionId) && _runtime is not null)
        {
            try
            {
                using var db = _runtime.CreateDbContext();
                var row = db.KioskMonitorSessions.SingleOrDefault(x => x.SessionId == fromSessionId && x.UserId == userId);
                if (row is not null)
                {
                    existing ??= new Entry(row.UserId, row.PlaylistId, row.ExpiresTs);
                    db.KioskMonitorSessions.Remove(row);
                    db.SaveChanges();
                }
            }
            catch { }
        }
        if (existing is null) return;
        var expires = http.User.GetTokenExpiresTs() ?? existing.Value.ExpiresTs;
        Upsert(to, existing.Value.UserId, existing.Value.PlaylistId, expires);
    }

    public bool IsLocked(HttpContext http)
    {
        EnsureFresh();
        // 重载失败或本任期尚未成功对齐时按失败关闭：拒绝下发，避免升主窗口里锁蒸发成可写。
        if (!IsReady()) return true;
        var key = SessionKey(http);
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!_sessions.TryGetValue(key, out var entry))
            return TryLoad(key);
        if (entry.ExpiresTs > 0 && entry.ExpiresTs <= Now())
        {
            Remove(key);
            return false;
        }
        return true;
    }

    public int? PlaylistId(HttpContext http)
    {
        EnsureFresh();
        var key = SessionKey(http);
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (_sessions.TryGetValue(key, out var entry) && (entry.ExpiresTs <= 0 || entry.ExpiresTs > Now()))
            return entry.PlaylistId;
        return null;
    }

    public void Reload() => TryReload();

    public bool IsReady() => _loadSucceeded && _loadedGeneration == _role.Generation;

    private bool TryReload()
    {
        if (!_replication.RuntimeSessionsTrusted)
        {
            // 运行库未追平或本任期还不能证明会话表完整：读到的行可能缺锁，不能标成已对齐。
            _loadSucceeded = false;
            return false;
        }
        if (_runtime is null)
        {
            _loadedGeneration = _role.Generation;
            _loadSucceeded = true;
            return true;
        }
        try
        {
            using var db = _runtime.CreateDbContext();
            var now = Now();
            var rows = db.KioskMonitorSessions.AsNoTracking().ToList();
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.ExpiresTs > 0 && row.ExpiresTs <= now)
                {
                    db.KioskMonitorSessions.Remove(db.KioskMonitorSessions.Find(row.SessionId)!);
                    continue;
                }
                _sessions[row.SessionId] = new Entry(row.UserId, row.PlaylistId, row.ExpiresTs);
                keep.Add(row.SessionId);
            }
            foreach (var key in _sessions.Keys)
                if (!keep.Contains(key)) _sessions.TryRemove(key, out _);
            db.SaveChanges();
            _loadedGeneration = _role.Generation;
            _loadSucceeded = true;
            return true;
        }
        catch
        {
            // 失败时不把任期标成已对齐，也不清空已有内存会话；IsLocked 按失败关闭拒绝下发并重试重载。
            _loadSucceeded = false;
            return false;
        }
    }

    private void EnsureFresh()
    {
        var generation = _role.Generation;
        if (_loadSucceeded && generation == _loadedGeneration) return;
        lock (_loadLock)
        {
            if (_loadSucceeded && generation == _loadedGeneration) return;
            TryReload();
        }
    }

    private bool TryLoad(string key)
    {
        if (_runtime is null) return false;
        try
        {
            using var db = _runtime.CreateDbContext();
            var row = db.KioskMonitorSessions.AsNoTracking().SingleOrDefault(x => x.SessionId == key);
            if (row is null) return false;
            if (row.ExpiresTs > 0 && row.ExpiresTs <= Now())
            {
                Remove(key);
                return false;
            }
            _sessions[key] = new Entry(row.UserId, row.PlaylistId, row.ExpiresTs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Upsert(string key, int userId, int playlistId, long expiresTs)
    {
        _sessions[key] = new Entry(userId, playlistId, expiresTs);
        if (_runtime is null) return;
        try
        {
            using var db = _runtime.CreateDbContext();
            var row = db.KioskMonitorSessions.Find(key);
            if (row is null)
            {
                db.KioskMonitorSessions.Add(new KioskMonitorSession
                {
                    SessionId = key, UserId = userId, PlaylistId = playlistId,
                    ExpiresTs = expiresTs, EnteredTs = Now()
                });
            }
            else
            {
                row.UserId = userId;
                row.PlaylistId = playlistId;
                row.ExpiresTs = expiresTs;
            }
            db.SaveChanges();
        }
        catch
        {
        }
    }

    private void Remove(string key)
    {
        _sessions.TryRemove(key, out _);
        if (_runtime is null) return;
        try
        {
            using var db = _runtime.CreateDbContext();
            var row = db.KioskMonitorSessions.Find(key);
            if (row is not null)
            {
                db.KioskMonitorSessions.Remove(row);
                db.SaveChanges();
            }
        }
        catch
        {
        }
    }

    private long Now() => _clock.GetUtcNow().ToUnixTimeMilliseconds();
}
