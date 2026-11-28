using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;

namespace Scada.Data.RuntimeCleanup;

public enum RuntimeCleanupPolicy
{
    /// <summary>删除对应配置实体时必须清理或关闭这些运行行。</summary>
    MustClean = 0,
    /// <summary>运行痕迹、审计或历史样本，删除配置时保留。</summary>
    Retain = 1
}

public sealed record RuntimeCleanupEntry(
    string RuntimeEntity,
    RuntimeCleanupPolicy Policy,
    IReadOnlyList<string> ConfigEntities,
    string Reason,
    string? Hook = null);

/// <summary>
/// 配置实体到必须清理的运行表的集中清单。历史库仍按测点编号存储，删除时不做在线大清理，因此不在本清单。
/// 新增运行表时必须在此登记，并用测试守门；MustClean 项可通过 <see cref="Register"/> 挂上清理实现。
/// </summary>
public static class RuntimeCleanupCatalog
{
    public static IReadOnlyList<RuntimeCleanupEntry> Entries { get; } =
    [
        Must<AlarmEvent>("Tag", "Device", "AlarmRule", "关闭仍在活动的报警；已结束记录保留。", "CloseOpenAlarmEvents"),
        Retain<AlarmActivity>("报警活动轨迹，删除规则后仍要能追查。"),
        Retain<AlarmRollup>("报警预聚合按测点编号保留，不做在线大清理。"),
        Retain<AlarmRollupState>("预聚合游标随库保留。"),
        Retain<AlarmNotificationLog>("通知发送记录。"),
        Retain<NotificationOutbox>("发件箱与失败重试记录。"),
        Retain<GatewayInboxBatch>("网关收件批次。"),
        Retain<LocalControlCommand>("本机控制命令留痕，接管核对仍要读取。"),
        Retain<GatewayControlCommand>("网关控制命令留痕。"),
        Retain<McpIdempotencyRecord>("MCP 幂等记录。"),
        Must<OeeLineSample>("ProductionLine", "删除产线时清掉样本。", "OeeEndpoints.DeleteLine"),
        Must<OeeRollup>("ProductionLine", "删除产线时清掉预聚合。", "OeeEndpoints.DeleteLine"),
        Retain<OeeRollupState>("全局滚动水位（Id = 1），不能按产线删除。"),
        Must<SpcSample>("SpcChart", "删除控制图时清掉样本。", "SpcChartDelete"),
        Must<SpcViolation>("SpcChart", "删除控制图时清掉违例。", "SpcChartDelete"),
        Retain<TraceabilityRecord>("追溯记录按任务编号保留，供事后查询。"),
        Retain<TraceabilityAttempt>("追溯尝试记录。"),
        Retain<RecipeDownload>("配方下发历史，接管核对仍要读取。"),
        Retain<ReportRun>("报表运行归档记录。"),
        Retain<ReportApproval>("报表审批记录。"),
        Must<CalcJobState>("CalcJob", "删除计算任务时清掉运行状态。", "CalcJobConfiguration.Delete"),
        Must<CalcJobSnapshot>("CalcJob", "删除计算任务时清掉快照。", "CalcJobConfiguration.Delete"),
        Retain<OperationLog>("操作审计，配置删除后仍要保留。"),
        Retain<QualityInspection>("检验记录可追溯，不随测点或产品删除而清理。"),
        Must<DowntimeEvent>("ProductionLine", "删除产线时清掉停机事件。", "OeeEndpoints.DeleteLine"),
        Must<ProductionCountCorrection>("ProductionLine", "删除产线时清掉产量修正。", "OeeEndpoints.DeleteLine"),
        Retain<ControlSequenceRun>("顺控运行历史，接管核对仍要读取。"),
        Retain<ProductionBatch>("生产批次记录，接管核对仍要读取。"),
        Retain<ControlRequest>("控制复核请求历史。"),
        Must<ControlLockout>("Tag", "Device", "删除测点或设备时解除仍有效的挂牌。", "ControlLockoutOnDelete"),
        Must<DeviceControlState>("Device", "删除设备时清掉控制状态。", "DeviceControlStateOnDelete"),
        Must<MomentaryLease>("Tag", "清理删除测点后残留的点动租约行；删除前须先由 MomentaryControlService 失电优先释放。", "MomentaryLeaseOnDelete"),
        Must<AnnunciatorSilence>("AnnunciatorPanel", "删除光字牌时清掉消音状态。", "AnnunciatorSilenceOnDelete"),
        Must<GatewayDataStream>("AcquisitionGateway", "删除网关时清掉数据流游标。", "GatewayDelete"),
        Must<GatewayArchiveCursor>("AcquisitionGateway", "删除网关时清掉归档游标。", "GatewayDelete"),
        Must<GatewayDeviceHealth>("Device", "AcquisitionGateway", "删除设备或网关时清掉健康行。", "GatewayDeviceAlarms")
    ];

    private static readonly ConcurrentDictionary<string, Func<RuntimeDbContext, IReadOnlyCollection<int>, CancellationToken, Task>> Hooks = new(StringComparer.Ordinal);

    public static IReadOnlyCollection<Type> RequiredRuntimeEntities() =>
        typeof(RuntimeDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .ToArray();

    /// <summary>为 MustClean 钩子名挂上实际清理。新增运行表时先在 Entries 登记，再在这里注册实现。</summary>
    public static void Register(string hook, Func<RuntimeDbContext, IReadOnlyCollection<int>, CancellationToken, Task> cleanup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hook);
        ArgumentNullException.ThrowIfNull(cleanup);
        Hooks[hook] = cleanup;
    }

    public static bool TryGetHook(string hook, out Func<RuntimeDbContext, IReadOnlyCollection<int>, CancellationToken, Task> cleanup) =>
        Hooks.TryGetValue(hook, out cleanup!);

    private static RuntimeCleanupEntry Must<T>(string config, string reason, string hook) =>
        new(typeof(T).Name, RuntimeCleanupPolicy.MustClean, [config], reason, hook);

    private static RuntimeCleanupEntry Must<T>(string config1, string config2, string reason, string hook) =>
        new(typeof(T).Name, RuntimeCleanupPolicy.MustClean, [config1, config2], reason, hook);

    private static RuntimeCleanupEntry Must<T>(string config1, string config2, string config3, string reason, string hook) =>
        new(typeof(T).Name, RuntimeCleanupPolicy.MustClean, [config1, config2, config3], reason, hook);

    private static RuntimeCleanupEntry Retain<T>(string reason) =>
        new(typeof(T).Name, RuntimeCleanupPolicy.Retain, [], reason);
}
