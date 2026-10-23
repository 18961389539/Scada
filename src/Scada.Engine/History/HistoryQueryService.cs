using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Engine.History;

public sealed record HistoryQueryResult(HistoryResolution Resolution, IReadOnlyList<HistorySeries> Series);

public sealed class HistoryQueryService
{
    private static readonly TimeSpan RawLimit = TimeSpan.FromHours(6);
    private static readonly TimeSpan FiveMinuteLimit = TimeSpan.FromDays(7);

    private readonly IHistoryStore _store;

    public HistoryQueryService(IHistoryStore store)
    {
        _store = store;
    }

    public async Task<HistoryQueryResult> QueryAsync(
        IReadOnlyCollection<int> tagIds,
        long from,
        long to,
        CancellationToken cancellationToken = default)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(to - from, 0));
        if (span <= RawLimit)
        {
            var series = await _store.QueryRawAsync(tagIds, from, to, cancellationToken);
            return new HistoryQueryResult(HistoryResolution.Raw, series);
        }

        if (span <= FiveMinuteLimit)
        {
            var series = await _store.QueryAggregate5MinAsync(tagIds, from, to, cancellationToken);
            return new HistoryQueryResult(HistoryResolution.FiveMinute, series);
        }

        var hourly = await _store.QueryAggregateHourAsync(tagIds, from, to, cancellationToken);
        return new HistoryQueryResult(HistoryResolution.Hour, hourly);
    }
}
