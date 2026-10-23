using Scada.Core.Entities;

namespace Scada.Core.Interfaces;

public interface IConfigSnapshot
{
    IReadOnlyList<Device> Devices { get; }
    IReadOnlyList<Tag> Tags { get; }
    IReadOnlyList<AlarmRule> AlarmRules { get; }
    int Version { get; }
    long LoadedAt { get; }
    Task ReloadAsync(CancellationToken cancellationToken = default);
    bool TryGetTag(int tagId, out Tag tag);
    bool TryGetDevice(int deviceId, out Device device);
}
