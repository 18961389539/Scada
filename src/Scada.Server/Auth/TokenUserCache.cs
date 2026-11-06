using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;

namespace Scada.Server.Auth;

// JWT 校验里"账号是否仍然有效"这一步原来每个请求都查一次 Users 表，
// 让 SQLite 成了鉴权热路径（每个 API、每个 SignalR 握手各一次）。
// 这里只缓存判定所需的三个字段，5 秒窗口内复用：改密、改角色、禁用最迟 5 秒生效，
// 与 JWT 自身有效期相比是可接受的延迟，而查询量下降几个数量级。
public sealed class TokenUserCache(IDbContextFactory<ConfigDbContext> factory, TimeProvider clock)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<int, Entry> _entries = new();

    private sealed record Entry(bool Enabled, UserRole Role, string CredentialsVersion, long ExpiresAt);

    // 与 JwtBearerEvents.OnTokenValidated 等价的三项判断：账号存在且启用、角色一致、凭据版本一致。
    public async Task<bool> IsValidAsync(int userId, string? role, string? credentialsVersion, CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (!_entries.TryGetValue(userId, out var entry) || entry.ExpiresAt <= now)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (user is null)
            {
                _entries.TryRemove(userId, out _);
                return false;
            }
            entry = new Entry(user.Enabled, user.Role, JwtTokenService.CredentialsVersion(user), now + (long)Ttl.TotalMilliseconds);
            _entries[userId] = entry;
        }

        return entry.Enabled
            && entry.Role.ToString() == role
            && entry.CredentialsVersion == credentialsVersion;
    }

    // 显式失效：改密、改角色、禁用/启用账号时由端点调用，避免等满 5 秒窗口。
    public void Invalidate(int userId) => _entries.TryRemove(userId, out _);

    public void InvalidateAll() => _entries.Clear();
}
