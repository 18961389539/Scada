using Scada.Core.Models;

namespace Scada.Core.Interfaces;

public interface IGatewayHistoryWriter
{
    Task WriteGatewayHistoryAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken);
}

public interface IHistoryStreamReader
{
    IAsyncEnumerable<HistoryValue> ReadAsync(IReadOnlyCollection<int> tagIds, long from, long to,
        HistoryResolution resolution, CancellationToken cancellationToken);

    // 历史复盘：取每个测点在指定时刻之前最后一条原始采样；默认实现保持旧存储兼容。
    Task<IReadOnlyList<HistoryValue>> LastBeforeAsync(IReadOnlyCollection<int> tagIds, long ts, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<HistoryValue>>([]);
}

public interface IHistoryStore
{
    Task WriteRawAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken);
    Task<IReadOnlyList<HistorySeries>> QueryRawAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken);
    Task WriteAggregate5MinAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken);
    Task WriteAggregateHourAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken);
    Task<IReadOnlyList<HistorySeries>> QueryAggregate5MinAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken);
    Task<IReadOnlyList<HistorySeries>> QueryAggregateHourAsync(IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken);
    Task<IReadOnlyList<AggregateValue>> QueryRawAggregatesAsync(long from, long to, CancellationToken cancellationToken);
    Task<IReadOnlyList<AggregateValue>> Query5MinAggregatesAsync(long from, long to, CancellationToken cancellationToken);
    Task<IReadOnlyList<AggregatePoint>> QueryHourAggregatePointsAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken);
    Task<IReadOnlyList<AggregatePoint>> QueryAggregatePointsAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution, CancellationToken cancellationToken);
}
/// <summary>
/// 历史库后端契约。查询、写入、保留清理、容量、备份打包和复制增量都走这里，
/// 上层不要依赖月库文件路径、rowid 或具体数据库产品。
/// 默认实现为 SQLite（SqliteHistoryBackend）；现有 IHistoryStore 查询/写入签名保持不变。
/// </summary>
public interface IHistoryBackend : IHistoryStore, IHistoryStreamReader, IGatewayHistoryWriter
{
    Task CleanupRetentionAsync(CancellationToken cancellationToken);

    HistoryStoreUsage MeasureUsage();

    /// <summary>
    /// 把后端自己的历史库文件打包到暂存目录（不含写入暂存 spool）。
    /// 返回写入的库文件完整路径，调用方负责计算清单哈希。
    /// </summary>
    Task<IReadOnlyList<string>> PackBackupAsync(string stagingHistoryDirectory, CancellationToken cancellationToken);

    IReadOnlyList<HistoryReplicaCursor> GetReplicaCursors();
    HistoryReplicaRows ReadReplica(string file, string table, long after, int limit);
    void ApplyReplica(HistoryReplicaRows batch);

    Task<IReadOnlyList<HistoryPendingWindow>> ListPendingWindowsAsync(
        bool raw, long latestStartInclusive, int batchSize, CancellationToken cancellationToken);

    Task AcknowledgePendingWindowAsync(HistoryPendingWindow window, CancellationToken cancellationToken);

    Task<bool> HasPendingRawWindowsAsync(long fromInclusive, long toExclusive, CancellationToken cancellationToken);

    /// <summary>按分辨率查询序列。默认转发到现有 IHistoryStore 方法，行为不变。</summary>
    Task<IReadOnlyList<HistorySeries>> QueryAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution, CancellationToken cancellationToken)
        => resolution switch
        {
            HistoryResolution.Raw => QueryRawAsync(tagIds, from, to, cancellationToken),
            HistoryResolution.FiveMinute => QueryAggregate5MinAsync(tagIds, from, to, cancellationToken),
            HistoryResolution.Hour => QueryAggregateHourAsync(tagIds, from, to, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(resolution))
        };
}
