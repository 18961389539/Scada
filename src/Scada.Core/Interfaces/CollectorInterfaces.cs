using Scada.Core.Entities;
using Scada.Core.Models;

namespace Scada.Core.Interfaces;

public interface IDeviceCredentials
{
    string Protect(string password);
    string Unprotect(string protectedPassword);
}

public interface IDeviceDriver : IAsyncDisposable
{
    bool IsConnected { get; }
    string? LastError { get; }
    Task ConnectAsync(Device device, CancellationToken cancellationToken = default);
    void Disconnect();
    Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default);
    Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default);
}

public interface IDeviceDriverFactory
{
    IDeviceDriver Create(Device device);
}

public interface IReadPlanBuilder
{
    ReadPlan Build(Device device, IReadOnlyList<Tag> tags);
}

public interface ICollectorControl
{
    IReadOnlyList<CollectorDeviceStatus> GetStatuses();
    Task<IReadOnlyList<TagValue>> ReadAsync(int deviceId, IReadOnlyList<int> tagIds, CancellationToken cancellationToken = default)
        => Task.FromException<IReadOnlyList<TagValue>>(new NotSupportedException("Immediate acquisition is not supported by this collector."));
    Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default);
    // 设备正在采集时复用其已打开的连接做一次探针读取；返回 null 表示未采集，调用方自行建立独立连接。
    Task<CollectorProbeResult?> ProbeAsync(int deviceId, Tag tag, CancellationToken cancellationToken = default)
        => Task.FromResult<CollectorProbeResult?>(null);
    Task<CommandExecutionResult> WriteIdentifiedAsync(int deviceId, Tag tag, double value, CollectorCommandContext context, CancellationToken cancellationToken = default)
        => WriteAsync(deviceId, tag, value, cancellationToken);
    void RequestReload();
}

public interface ICollectorDiagnostics
{
    CollectorDeviceDiagnostics? GetDiagnostics(int deviceId);
    bool RequestReconnect(int deviceId);
}

public interface ICollectorJournal
{
    Task AppendAsync(int deviceId, IReadOnlyList<TagValue> values, CancellationToken cancellationToken = default);
}
