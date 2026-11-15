using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

// 实时缓存在读取时做"配置已变"的降级判定：计算测点被停用/重算未跟上、物理测点量程变了，
// 都必须返回 Uncertain 而不是把旧值当好值推给画面与报警。
public sealed class RealtimeCacheTests
{
    private const long ConfigLoadedAt = 1_000;

    private sealed class Snapshot(params Tag[] tags) : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices => [];
        public IReadOnlyList<Tag> Tags => tags;
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1;
        // 注意不能叫 LoadedAt => LoadedAt：属性名会遮蔽外层常量，变成无限递归。
        public long LoadedAt => ConfigLoadedAt;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag tag)
        {
            foreach (var candidate in tags)
            {
                if (candidate.Id != tagId) continue;
                tag = candidate;
                return true;
            }
            tag = null!;
            return false;
        }
        public bool TryGetDevice(int deviceId, out Device device) { device = null!; return false; }
    }

    [Fact]
    public void CalculatedTagDegradesWhenDisabledOrRevisionOrTimestampLags()
    {
        var tag = new Tag { Id = 1, Kind = TagKind.Calculated, CalculationEnabled = true, CalculationRevision = 5 };
        var cache = new RealtimeCache(new Snapshot(tag));

        cache.Set(new TagValue(1, 42, TagQuality.Good, ConfigLoadedAt + 1, DefinitionRevision: 5));
        Assert.Equal(TagQuality.Good, cache.Get(1)!.Quality);

        // 重算还没跟上新定义。
        cache.Set(new TagValue(1, 42, TagQuality.Good, ConfigLoadedAt + 1, DefinitionRevision: 4));
        var stale = cache.Get(1)!;
        Assert.Equal(TagQuality.Uncertain, stale.Quality);
        Assert.NotNull(stale.Error);

        // 值来自上一次配置加载之前。
        cache.Set(new TagValue(1, 42, TagQuality.Good, ConfigLoadedAt, DefinitionRevision: 5));
        Assert.Equal(TagQuality.Uncertain, cache.Get(1)!.Quality);

        // 计算被停用。
        tag.CalculationEnabled = false;
        cache.Set(new TagValue(1, 42, TagQuality.Good, ConfigLoadedAt + 1, DefinitionRevision: 5));
        Assert.Equal(TagQuality.Uncertain, cache.Get(1)!.Quality);
    }

    [Fact]
    public void PhysicalTagDegradesWhenRangeRevisionChanged()
    {
        var tag = new Tag { Id = 2, Kind = TagKind.Physical, CalculationRevision = 7 };
        var cache = new RealtimeCache(new Snapshot(tag));

        cache.Set(new TagValue(2, 10, TagQuality.Good, ConfigLoadedAt + 1, DefinitionRevision: 7));
        Assert.Equal(TagQuality.Good, cache.Get(2)!.Quality);

        // 量程/换算改了，旧采样不能按新量程解释。
        cache.Set(new TagValue(2, 10, TagQuality.Good, ConfigLoadedAt + 1, DefinitionRevision: 6));
        var degraded = cache.Get(2)!;
        Assert.Equal(TagQuality.Uncertain, degraded.Quality);
        Assert.Contains("量程", degraded.Error);
    }

    [Fact]
    public void ValuesWithoutRevisionAreNotDegraded()
    {
        var cache = new RealtimeCache(new Snapshot(new Tag { Id = 3, Kind = TagKind.Physical, CalculationRevision = 9 }));
        // 网关/仿真等来源不带 DefinitionRevision，保持原质量。
        cache.Set(new TagValue(3, 1, TagQuality.Good, ConfigLoadedAt + 1));
        Assert.Equal(TagQuality.Good, cache.Get(3)!.Quality);
    }

    [Fact]
    public void SnapshotAndGetManyAgreeWithGet()
    {
        var cache = new RealtimeCache(new Snapshot(new Tag { Id = 1, Kind = TagKind.Calculated, CalculationRevision = 1 }));
        cache.Set(new TagValue(1, 1, TagQuality.Good, ConfigLoadedAt + 1, DefinitionRevision: 1));
        cache.Set(new TagValue(2, 2, TagQuality.Bad, ConfigLoadedAt + 1));
        cache.Set(new TagValue(3, 3, TagQuality.Good, ConfigLoadedAt + 1));

        var snapshot = cache.Snapshot();
        Assert.Equal(3, snapshot.Count);
        foreach (var (id, value) in snapshot) Assert.Equal(cache.Get(id)!.Quality, value.Quality);

        var many = cache.GetMany([1, 2, 3, 999]);
        Assert.Equal(3, many.Count);
        Assert.Equal([1, 2, 3], many.Select(x => x.TagId));
    }
}
