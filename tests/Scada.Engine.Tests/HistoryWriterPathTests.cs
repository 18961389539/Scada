using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Acquisition;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Engine.History;
using Scada.Engine.Realtime;

namespace Scada.Engine.Tests;

public sealed class HistoryWriterPathTests
{
    [Fact]
    public void OfficialMembershipFollowsTheActivePathNotTheConfiguredGateway()
    {
        var device = new Device
        {
            Id = 1, Name = "泵", GatewayId = 7, Host = "10.0.0.1", Port = 502,
            StandbyHost = "10.0.0.8", StandbyPort = 502, Enabled = true,
            FailoverHoldMs = 10_000, SwitchbackHoldMs = 30_000
        };
        var tag = new Tag { Id = 11, DeviceId = 1, Name = "F", DataType = TagDataType.Float, ArchiveMode = ArchiveMode.OnChange };
        var snapshot = new Snapshot([device], [tag]);
        var paths = new MutablePaths();
        using var dir = new TempDir();
        using var writer = new HistoryWriterService(
            new RealtimeBus(), new RealtimeCache(), snapshot, new NoopStore(),
            Options.Create(new ScadaSettings { ArchiveFlushIntervalMs = 200, HistoryRoot = dir.Path }),
            new HistorySpool(dir.Path), NullLogger<HistoryWriterService>.Instance, paths: paths);

        writer.ReloadMembershipForTests();
        Assert.Empty(writer.LocalTagIdsForTests());

        paths.Path = AcquisitionPathKind.Standby;
        paths.Bump();
        writer.ReloadMembershipForTests();
        Assert.Equal([11], writer.LocalTagIdsForTests());
    }

    private sealed class Snapshot(IReadOnlyList<Device> devices, IReadOnlyList<Tag> tags) : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices { get; } = devices;
        public IReadOnlyList<Tag> Tags { get; } = tags;
        public IReadOnlyList<AlarmRule> AlarmRules { get; } = [];
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
            device = Devices.FirstOrDefault(x => x.Id == deviceId)!;
            return device is not null;
        }
    }

    private sealed class MutablePaths : IDeviceAcquisitionPaths
    {
        public AcquisitionPathKind Path { get; set; } = AcquisitionPathKind.Primary;
        public int Version { get; private set; }
        public void Bump() => Version++;
        public DeviceAcquisitionRoute Resolve(Device device) =>
            Path == AcquisitionPathKind.Standby ? DeviceAcquisitionRules.StandbyOf(device) : DeviceAcquisitionRules.PrimaryOf(device);
        public Device Apply(Device device) => DeviceAcquisitionRules.Apply(device, Resolve(device));
        public bool HasStandby(Device device) => DeviceAcquisitionRules.HasStandby(device);
        public AcquisitionPathKind Current(int deviceId) => Path;
        public event Action<int, AcquisitionPathKind, string>? PathChanged { add { } remove { } }
    }

    private sealed class NoopStore : IHistoryStore
    {
        public Task WriteRawAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<HistorySeries>> QueryRawAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistorySeries>>([]);
        public Task WriteAggregate5MinAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WriteAggregateHourAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<HistorySeries>> QueryAggregate5MinAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistorySeries>>([]);
        public Task<IReadOnlyList<HistorySeries>> QueryAggregateHourAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HistorySeries>>([]);
        public Task<IReadOnlyList<AggregateValue>> QueryRawAggregatesAsync(long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AggregateValue>>([]);
        public Task<IReadOnlyList<AggregateValue>> Query5MinAggregatesAsync(long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AggregateValue>>([]);
        public Task<IReadOnlyList<AggregatePoint>> QueryHourAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AggregatePoint>>([]);
        public Task<IReadOnlyList<AggregatePoint>> QueryAggregatePointsAsync(IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AggregatePoint>>([]);
    }
}
