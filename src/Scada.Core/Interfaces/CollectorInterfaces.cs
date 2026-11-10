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

/// <summary>
/// 一次总线事务的调度钩子。共享同一物理链路的驱动在每个「请求/响应」单位前后调用，
/// 把互斥粒度从「整份读取计划」细化到「单个事务」。
/// 否则一台有几十个读块的设备每轮独占总线数百毫秒，同总线上 100ms 周期的设备会被拖成秒级。
/// </summary>
public interface ITransactionGate
{
    /// <param name="ownerId">发起本次事务的设备，用于按成员统计排队与占用。</param>
    T Execute<T>(int ownerId, Func<T> operation, CancellationToken cancellationToken);
}

/// <summary>
/// 支持按事务加锁的驱动。未实现此接口的驱动由 ChannelBus 退化为「整次操作加锁」，行为与共享通道之前一致。
/// </summary>
public interface ITransactionGatedDriver
{
    /// <summary>由总线在创建驱动后注入一次；驱动内部不得替换。</summary>
    ITransactionGate? TransactionGate { set; }
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
    // 共享通道的总线级观测：占用率、排队、成员争用。没有运行中的通道时返回全零快照。
    ChannelStatistics GetChannelStatistics(int channelId)
        => new(channelId, "", "", "", false, 0, 0, 0, 0, 0, 0, 0, null, ScadaTime.NowMs(), []);
}

public interface ICollectorJournal
{
    Task AppendAsync(int deviceId, IReadOnlyList<TagValue> values, CancellationToken cancellationToken = default);
}
