using Scada.Data.Infrastructure;

namespace Scada.Data.Tests;

/// <summary>
/// 授权配额缓存过期后仍须用旧计数拦住超限，不能把 TTL 未命中当成放行。
/// </summary>
public sealed class LicenseQuotaCounterTests
{
    [Fact]
    public void ExpiredCacheStillReturnsLastCountsForEnforcement()
    {
        var counter = new LicenseQuotaCounter(null!);
        Assert.False(counter.TryGetCached(out _, out _));
        Assert.False(counter.IsFresh);

        counter.SeedForTests(devices: 8, tags: 40);
        Assert.True(counter.TryGet(out var devices, out var tags));
        Assert.Equal(8, devices);
        Assert.Equal(40, tags);
        Assert.True(counter.TryGetCached(out devices, out tags));
        Assert.True(counter.IsFresh);

        counter.ExpireForTests();
        Assert.False(counter.TryGet(out _, out _));
        Assert.False(counter.IsFresh);
        Assert.True(counter.TryGetCached(out devices, out tags));
        Assert.Equal(8, devices);
        Assert.Equal(40, tags);

        counter.Adjust(2, -5);
        Assert.True(counter.IsFresh);
        Assert.True(counter.TryGetCached(out devices, out tags));
        Assert.Equal(10, devices);
        Assert.Equal(35, tags);
    }
}
