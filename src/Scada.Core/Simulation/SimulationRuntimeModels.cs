using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Core.Simulation;

public sealed record SimulationItem(int TagId, string Name, TagDataType DataType, double Value, bool Forced, TagQuality Quality, string? Error);
public sealed record SimulationSnapshot(int DeviceId, bool Enabled, SimulationFaultKind Fault, long FaultUntilTs, IReadOnlyList<SimulationItem> Items);

/// <summary>设备仿真运行时：强制值、故障注入与快照。波形生成仍由采集侧实现。</summary>
public interface ISimulationRuntime
{
    SimulationSnapshot Snapshot(Device device, SimulationDeviceOptions options, IReadOnlyList<Tag> tags);
    void Force(int deviceId, int tagId, double value);
    bool Release(int deviceId, int tagId);
    int ReleaseAll(int deviceId);
    void InjectFault(int deviceId, SimulationFaultKind kind, int durationSec);
    void ClearFault(int deviceId);
    void Remove(int deviceId);
}
