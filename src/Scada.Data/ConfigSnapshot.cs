using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Data;

public sealed class ConfigSnapshot : IConfigSnapshot
{
    private readonly IDbContextFactory<ConfigDbContext> _contextFactory;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile SnapshotState _state = new([], [], [], [], 0, 0, 0, "", "");
    private int _version;
    private int _collectionVersion;

    public ConfigSnapshot(IDbContextFactory<ConfigDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public IReadOnlyList<Device> Devices => _state.Devices;
    public IReadOnlyList<Tag> Tags => _state.Tags;
    public IReadOnlyList<Area> Areas => _state.Areas;
    public IReadOnlyList<AlarmRule> AlarmRules => _state.AlarmRules;
    public int Version => _state.Version;
    public int CollectionVersion => _state.CollectionVersion;
    public long LoadedAt => _state.LoadedAt;

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var devices = await db.Devices.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
            var tags = await db.Tags.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
            var areas = await db.Areas.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
            var rules = await db.AlarmRules.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
            var previous = _state;
            // 内容和上次一样就不动版本号：一次保存会连续触发多轮重载，
            // 只改画面/报表/报警规则时也不该让采集引擎断开现场设备。
            var collectionFingerprint = CollectionFingerprint.Of(devices, tags);
            var fullFingerprint = CollectionFingerprint.OfAll(devices, tags, areas, rules);
            var version = previous.FullFingerprint == fullFingerprint ? previous.Version : Interlocked.Increment(ref _version);
            var collectionVersion = previous.CollectionFingerprint == collectionFingerprint
                ? previous.CollectionVersion
                : Interlocked.Increment(ref _collectionVersion);
            _state = new SnapshotState(devices, tags, areas, rules, version, collectionVersion, ScadaTime.NowMs(), fullFingerprint, collectionFingerprint);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public bool TryGetTag(int tagId, out Tag tag)
    {
        if (_state.TagMap.TryGetValue(tagId, out var found))
        {
            tag = found;
            return true;
        }

        tag = null!;
        return false;
    }

    public bool TryGetDevice(int deviceId, out Device device)
    {
        if (_state.DeviceMap.TryGetValue(deviceId, out var found))
        {
            device = found;
            return true;
        }

        device = null!;
        return false;
    }

    public bool TryGetArea(int areaId, out Area area)
    {
        if (_state.AreaMap.TryGetValue(areaId, out var found))
        {
            area = found;
            return true;
        }

        area = null!;
        return false;
    }

    private sealed record SnapshotState(
        IReadOnlyList<Device> Devices,
        IReadOnlyList<Tag> Tags,
        IReadOnlyList<Area> Areas,
        IReadOnlyList<AlarmRule> AlarmRules,
        int Version,
        int CollectionVersion,
        long LoadedAt,
        string FullFingerprint,
        string CollectionFingerprint)
    {
        public Dictionary<int, Tag> TagMap { get; } = Tags.ToDictionary(x => x.Id);
        public Dictionary<int, Device> DeviceMap { get; } = Devices.ToDictionary(x => x.Id);
        public Dictionary<int, Area> AreaMap { get; } = Areas.ToDictionary(x => x.Id);
    }
}
