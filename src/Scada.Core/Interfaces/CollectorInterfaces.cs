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
    Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default);
    void RequestReload();
}
