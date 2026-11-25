using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Server.External.Slave;

namespace Scada.Server.Tests;

public sealed class ExternalModbusMapTests
{
    [Fact]
    public void MultiRegisterOverlapIsSkippedWithWarning()
    {
        var config = Config(
            new Tag { Id = 1, Name = "narrow", Kind = TagKind.Physical, Area = TagArea.HoldingRegister, Address = 11, DataType = TagDataType.Int16 },
            new Tag { Id = 2, Name = "wide", Kind = TagKind.Physical, Area = TagArea.HoldingRegister, Address = 10, DataType = TagDataType.Float });
        var map = Build(config);

        Assert.True(map.Modbus.HoldingRegisters.ContainsKey(11));
        Assert.Equal(1, map.Modbus.HoldingRegisters[11].TagId);
        Assert.False(map.Modbus.HoldingRegisters.ContainsKey(10));
        Assert.DoesNotContain(map.Modbus.HoldingRegisters.Values, slot => slot.TagId == 2);
        Assert.Contains(map.Warnings, warning => warning.Contains("重叠", StringComparison.Ordinal) && warning.Contains("wide", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiRegisterAddressOverflowIsRejected()
    {
        var config = Config(new Tag { Id = 3, Name = "edge", Kind = TagKind.Physical, Area = TagArea.HoldingRegister, Address = 65535, DataType = TagDataType.Float });
        var map = Build(config);

        Assert.Empty(map.Modbus.HoldingRegisters);
        Assert.Contains(map.Warnings, warning => warning.Contains("65535", StringComparison.Ordinal) && warning.Contains("超出", StringComparison.Ordinal));
    }

    [Fact]
    public void FloatAtLastValidStartOccupiesBothRegisters()
    {
        var config = Config(new Tag { Id = 4, Name = "fit", Kind = TagKind.Physical, Area = TagArea.HoldingRegister, Address = 65534, DataType = TagDataType.Float });
        var map = Build(config);

        Assert.Equal(4, map.Modbus.HoldingRegisters[65534].TagId);
        Assert.Equal(4, map.Modbus.HoldingRegisters[65535].TagId);
        Assert.Empty(map.Warnings);
    }

    private static ExportPointMap Build(StubConfig config)
    {
        var map = new ExportPointMap(config, new StubCache());
        map.Ensure(new ModbusSlaveOptions { AutoAddressing = true, Points = [] }, new Iec104ServerOptions());
        return map;
    }

    private static StubConfig Config(params Tag[] tags) => new(tags);

    private sealed class StubConfig(params Tag[] tags) : IConfigSnapshot
    {
        public IReadOnlyList<Tag> Tags { get; } = tags;
        public IReadOnlyList<Device> Devices => [];
        public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag tag)
        {
            tag = Tags.FirstOrDefault(x => x.Id == tagId)!;
            return tag is not null;
        }
        public bool TryGetDevice(int deviceId, out Device device)
        {
            device = null!;
            return false;
        }
    }

    private sealed class StubCache : IRealtimeCache
    {
        public void Set(TagValue value) { }
        public TagValue? Get(int tagId) => null;
        public IReadOnlyList<TagValue> GetMany(IEnumerable<int> tagIds) => [];
        public IReadOnlyDictionary<int, TagValue> Snapshot() => new Dictionary<int, TagValue>();
    }
}
