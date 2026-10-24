using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class SessionSecurityTests
{
    [Fact]
    public void LoginIsLockedAfterRepeatedFailuresAndUnlockedAfterTheWindow()
    {
        var now = 1_000_000L;
        var throttle = new LoginThrottle(() => now);
        var key = LoginThrottle.Key("Admin", "10.0.0.8");

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) throttle.RecordFailure(key);
        Assert.False(throttle.IsLocked(key, out _));

        throttle.RecordFailure(key);
        Assert.True(throttle.IsLocked(key, out var retryAfter));
        Assert.Equal(LoginThrottle.LockMs, (long)retryAfter.TotalMilliseconds);

        now += LoginThrottle.LockMs - 1;
        Assert.True(throttle.IsLocked(key, out _));

        now += 1;
        Assert.False(throttle.IsLocked(key, out _));
    }

    [Fact]
    public void SuccessfulLoginClearsTheCounterAndKeysAreIsolated()
    {
        var now = 1_000_000L;
        var throttle = new LoginThrottle(() => now);
        var key = LoginThrottle.Key("operator", "10.0.0.8");
        var other = LoginThrottle.Key("operator", "10.0.0.9");

        for (var i = 0; i < LoginThrottle.MaxFailures; i++) throttle.RecordFailure(key);
        Assert.True(throttle.IsLocked(key, out _));
        Assert.False(throttle.IsLocked(other, out _));

        now += LoginThrottle.LockMs + 1;
        throttle.RecordFailure(key); throttle.Reset(key);
        Assert.False(throttle.IsLocked(key, out _));
    }

    [Fact]
    public void FailuresOutsideTheWindowDoNotAccumulate()
    {
        var now = 1_000_000L;
        var throttle = new LoginThrottle(() => now);
        var key = LoginThrottle.Key("admin", null);

        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            throttle.RecordFailure(key);
            now += LoginThrottle.WindowMs;
        }

        Assert.False(throttle.IsLocked(key, out _));
    }

    [Fact]
    public void RevokingOneUserOnlyAbortsThatUsersConnections()
    {
        var registry = new SessionRegistry();
        var aborted = new List<int>();
        registry.Register("c1", 7, "Operator", "v1", () => aborted.Add(1));
        registry.Register("c2", 7, "Operator", "v1", () => aborted.Add(2));
        registry.Register("c3", 8, "Viewer", "v1", () => aborted.Add(3));

        Assert.Equal(2, registry.RevokeUser(7));
        Assert.Equal(new[] { 1, 2 }, aborted);
        Assert.Equal(1, registry.Count);
        Assert.Equal(0, registry.RevokeUser(7));
    }

    [Fact]
    public void UnregisterAndFailingAbortCallbacksDoNotBreakOtherRevocations()
    {
        var registry = new SessionRegistry();
        registry.Register("c1", 1, "Admin", "v1", () => throw new InvalidOperationException("connection already gone"));
        registry.Register("c2", 1, "Admin", "v1", () => { });
        registry.Register("c3", 2, "Admin", "v1", () => { });
        registry.Unregister("c3");

        Assert.Equal(2, registry.RevokeUser(1));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ReaperAbortsDisabledDemotedAndDeletedUsersButKeepsValidSessions()
    {
        var path = Path.Combine(Path.GetTempPath(), "scada-session-" + Guid.NewGuid().ToString("N"), "config.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var factory = new Factory(path);
            int disabledId, demotedId, deletedId, validId;
            string validVersion;
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync();
                var disabled = new User { UserName = "disabled", Role = UserRole.Operator, PasswordHash = "hash-a" };
                var demoted = new User { UserName = "demoted", Role = UserRole.Admin, PasswordHash = "hash-b" };
                var deleted = new User { UserName = "deleted", Role = UserRole.Viewer, PasswordHash = "hash-c" };
                var valid = new User { UserName = "valid", Role = UserRole.Viewer, PasswordHash = "hash-d" };
                db.Users.AddRange(disabled, demoted, deleted, valid);
                await db.SaveChangesAsync();
                disabledId = disabled.Id; demotedId = demoted.Id; deletedId = deleted.Id; validId = valid.Id;
                validVersion = JwtTokenService.CredentialsVersion(valid.PasswordHash);
                disabled.Enabled = false; demoted.Role = UserRole.Viewer; db.Users.Remove(deleted);
                await db.SaveChangesAsync();
            }

            var aborted = new List<string>();
            var registry = new SessionRegistry();
            registry.Register("c-disabled", disabledId, "Operator", JwtTokenService.CredentialsVersion("hash-a"), () => aborted.Add("c-disabled"));
            registry.Register("c-demoted", demotedId, "Admin", JwtTokenService.CredentialsVersion("hash-b"), () => aborted.Add("c-demoted"));
            registry.Register("c-deleted", deletedId, "Viewer", JwtTokenService.CredentialsVersion("hash-c"), () => aborted.Add("c-deleted"));
            registry.Register("c-valid", validId, "Viewer", validVersion, () => aborted.Add("c-valid"));

            var reaper = new SessionReaper(registry, factory, NullLogger<SessionReaper>.Instance);
            var revoked = await reaper.SweepAsync(default);

            Assert.Equal(3, revoked);
            Assert.Contains("c-disabled", aborted);
            Assert.Contains("c-demoted", aborted);
            Assert.Contains("c-deleted", aborted);
            Assert.DoesNotContain("c-valid", aborted);
            Assert.Equal(1, registry.Count);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
        }
    }

    private sealed class Factory(string path) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
