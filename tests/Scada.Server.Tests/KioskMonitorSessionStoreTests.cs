using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Interfaces;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Tests;

public sealed class KioskMonitorSessionStoreTests
{
    [Fact]
    public async Task SessionsAreBoundPerTokenAndExpireIndependently()
    {
        await using var connection = TestRuntime.Open();
        var factory = TestRuntime.Factory(connection);
        var store = new KioskMonitorSessionStore(factory);
        var first = Http(3, "session-a", DateTimeOffset.UtcNow.AddHours(1));
        var second = Http(3, "session-b", DateTimeOffset.UtcNow.AddHours(1));
        var expired = Http(3, "session-c", DateTimeOffset.UtcNow.AddMinutes(-1));

        store.Enter(first, playlistId: 11);
        store.Enter(second, playlistId: 11);
        store.Enter(expired, playlistId: 11);

        Assert.True(store.IsLocked(first));
        Assert.True(store.IsLocked(second));
        Assert.False(store.IsLocked(expired));

        store.Leave(first);
        Assert.False(store.IsLocked(first));
        Assert.True(store.IsLocked(second));
    }

    [Fact]
    public async Task LeaveUserClearsEverySessionAndReloadRebuildsFromRuntime()
    {
        await using var connection = TestRuntime.Open();
        var factory = TestRuntime.Factory(connection);
        var role = new MutableRole();
        var store = new KioskMonitorSessionStore(factory, role);
        var first = Http(8, "keep-a", DateTimeOffset.UtcNow.AddHours(2));
        var other = Http(9, "keep-b", DateTimeOffset.UtcNow.AddHours(2));
        store.Enter(first, 21);
        store.Enter(other, 22);
        Assert.True(store.IsLocked(first));
        Assert.True(store.IsLocked(other));

        store.LeaveUser(8);
        Assert.False(store.IsLocked(first));
        Assert.True(store.IsLocked(other));

        role.Bump();
        var promoted = new KioskMonitorSessionStore(factory, role);
        Assert.True(promoted.IsLocked(other), "升主后应从运行库重建监视会话，不能把锁蒸发成可下发");
        Assert.False(promoted.IsLocked(first));
    }

    [Fact]
    public async Task RefreshTransfersTheLockToTheNewToken()
    {
        await using var connection = TestRuntime.Open();
        var factory = TestRuntime.Factory(connection);
        var store = new KioskMonitorSessionStore(factory);
        var old = Http(4, "old-jti", DateTimeOffset.UtcNow.AddHours(1));
        store.Enter(old, 5);
        var refreshed = Http(4, "new-jti", DateTimeOffset.UtcNow.AddHours(8));
        store.Transfer("old-jti", refreshed);
        Assert.False(store.IsLocked(old));
        Assert.True(store.IsLocked(refreshed));
        Assert.Equal(5, store.PlaylistId(refreshed));
    }

    [Fact]
    public async Task ReloadFailureDeniesControlUntilASuccessfulReload()
    {
        await using var connection = TestRuntime.Open();
        var inner = TestRuntime.Factory(connection);
        var role = new MutableRole();
        var factory = new FlipFactory(inner);
        var store = new KioskMonitorSessionStore(factory, role);
        var session = Http(3, "wall", DateTimeOffset.UtcNow.AddHours(1));
        var other = Http(4, "engineer", DateTimeOffset.UtcNow.AddHours(1));
        store.Enter(session, 11);
        Assert.True(store.IsLocked(session));
        Assert.False(store.IsLocked(other));

        factory.Fail = true;
        role.Bump();
        Assert.True(store.IsLocked(session), "重载失败时应拒绝下发，不能把锁蒸发成可写");
        Assert.True(store.IsLocked(other), "重载失败时按失败关闭，在对齐完成前拒绝所有下发");

        factory.Fail = false;
        Assert.True(store.IsLocked(session));
        Assert.False(store.IsLocked(other));
    }

    private static DefaultHttpContext Http(int userId, string session, DateTimeOffset expires)
    {
        var http = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, session),
            new Claim(JwtRegisteredClaimNames.Exp, expires.ToUnixTimeSeconds().ToString())
        ], "Bearer");
        http.User = new ClaimsPrincipal(identity);
        return http;
    }

    private sealed class MutableRole : INodeRole
    {
        public bool IsActive => true;
        public long Generation { get; private set; }
        public void Bump() => Generation++;
    }

    private sealed class FlipFactory(IDbContextFactory<RuntimeDbContext> inner) : IDbContextFactory<RuntimeDbContext>
    {
        public bool Fail { get; set; }
        public RuntimeDbContext CreateDbContext()
        {
            if (Fail) throw new InvalidOperationException("runtime unavailable");
            return inner.CreateDbContext();
        }
        public Task<RuntimeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Fail ? throw new InvalidOperationException("runtime unavailable") : inner.CreateDbContextAsync(cancellationToken);
    }
}
