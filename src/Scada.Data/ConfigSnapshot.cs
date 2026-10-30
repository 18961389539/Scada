using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Data;

public sealed class ConfigSnapshot : IConfigSnapshot
{
    private readonly IDbContextFactory<ConfigDbContext> _contextFactory;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile SnapshotState _state = new([], [], [], [], 0, 0);
    private int _version;

    public ConfigSnapshot(IDbContextFactory<ConfigDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public IReadOnlyList<Device> Devices => _state.Devices;
    public IReadOnlyList<Tag> Tags => _state.Tags;
    public IReadOnlyList<Area> Areas => _state.Areas;
    public IReadOnlyList<AlarmRule> AlarmRules => _state.AlarmRules;
    public int Version => _state.Version;
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
            var version = Interlocked.Increment(ref _version);
            _state = new SnapshotState(devices, tags, areas, rules, version, ScadaTime.NowMs());
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
        long LoadedAt)
    {
        public Dictionary<int, Tag> TagMap { get; } = Tags.ToDictionary(x => x.Id);
        public Dictionary<int, Device> DeviceMap { get; } = Devices.ToDictionary(x => x.Id);
        public Dictionary<int, Area> AreaMap { get; } = Areas.ToDictionary(x => x.Id);
    }
}
