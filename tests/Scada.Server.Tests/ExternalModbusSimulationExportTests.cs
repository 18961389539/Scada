using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Server.External.Slave;

namespace Scada.Server.Tests;

public sealed class ExternalModbusSimulationExportTests
{
    [Fact]
    public void Modbus_uses_bad_quality_substitute_for_simulated_values()
    {
        var cache = new StubCache(new TagValue(1, 12.5, TagQuality.Good, ScadaTime.NowMs(), Simulated: true));
        var map = Map(cache);

        var sample = map.Read(Slot(), badQualityValue: -1, TagQuality.Uncertain);

        Assert.Equal(-1, sample.Value);
        Assert.Equal(TagQuality.Uncertain, sample.Quality);
    }

    [Fact]
    public void Modbus_holds_last_official_good_when_value_becomes_simulated()
    {
        var official = new TagValue(1, 42, TagQuality.Good, ScadaTime.NowMs(), Simulated: false);
        var cache = new StubCache(official);
        var map = Map(cache);
        var officialSample = map.Read(Slot(), badQualityValue: null);
        Assert.Equal(42, officialSample.Value);

        cache.Value = official with { Value = 99, Simulated = true, Ts = ScadaTime.NowMs() };
        var simulated = map.Read(Slot(), badQualityValue: null);

        Assert.Equal(42, simulated.Value);
        Assert.Equal(TagQuality.Uncertain, simulated.Quality);
    }

    [Fact]
    public void Iec104_still_exports_simulated_value_with_non_good_quality()
    {
        var cache = new StubCache(new TagValue(1, 12.5, TagQuality.Good, ScadaTime.NowMs(), Simulated: true));
        var map = Map(cache);

        var sample = map.ReadIec(new ExportPointMap.IecSlot(1, "jog", TagDataType.Float, false, 1, Iec104PointKind.MeasuredFloat));

        Assert.Equal(12.5, sample.Value);
        Assert.Equal(TagQuality.Uncertain, sample.Quality);
    }

    private static ExportPointMap Map(StubCache cache)
    {
        var tag = new Tag { Id = 1, Name = "jog", Kind = TagKind.Physical, Area = TagArea.HoldingRegister, Address = 10, DataType = TagDataType.Float, DeviceId = 1 };
        var device = new Device { Id = 1, Name = "PLC", Enabled = true };
        var map = new ExportPointMap(new StubConfig(tag, device), cache);
        map.Ensure(new ModbusSlaveOptions { AutoAddressing = true, Points = [] }, new Iec104ServerOptions());
        return map;
    }

    private static ExportPointMap.ExportSlot Slot() =>
        new(1, "jog", TagDataType.Float, false, 10, 1);

    private sealed class StubConfig(Tag tag, Device device) : IConfigSnapshot
    {
        public IReadOnlyList<Tag> Tags { get; } = [tag];
        public IReadOnlyList<Device> Devices { get; } = [device];
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag found)
        {
            found = tagId == tag.Id ? tag : null!;
            return found is not null;
        }
        public bool TryGetDevice(int deviceId, out Device found)
        {
            found = deviceId == device.Id ? device : null!;
            return found is not null;
        }
    }

    private sealed class StubCache(TagValue value) : IRealtimeCache
    {
        public TagValue Value { get; set; } = value;
        public void Set(TagValue next) => Value = next;
        public TagValue? Get(int tagId) => tagId == Value.TagId ? Value : null;
        public IReadOnlyList<TagValue> GetMany(IEnumerable<int> tagIds) => tagIds.Contains(Value.TagId) ? [Value] : [];
        public IReadOnlyDictionary<int, TagValue> Snapshot() => new Dictionary<int, TagValue> { [Value.TagId] = Value };
    }
}
