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
