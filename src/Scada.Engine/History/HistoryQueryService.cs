using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Engine.History;

public sealed record HistoryQueryResult(
    HistoryResolution Resolution,
    IReadOnlyList<HistorySeries> Series,
    bool Downsampled = false,
    int PointLimit = 0);

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
        CancellationToken cancellationToken = default,
        int maxPoints = 0)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(to - from, 0));
        if (span <= RawLimit)
        {
            return Limit(HistoryResolution.Raw, await _store.QueryRawAsync(tagIds, from, to, cancellationToken), maxPoints);
        }

        if (span <= FiveMinuteLimit)
        {
            return Limit(HistoryResolution.FiveMinute, await _store.QueryAggregate5MinAsync(tagIds, from, to, cancellationToken), maxPoints);
        }

        return Limit(HistoryResolution.Hour, await _store.QueryAggregateHourAsync(tagIds, from, to, cancellationToken), maxPoints);
    }

    private static HistoryQueryResult Limit(HistoryResolution resolution, IReadOnlyList<HistorySeries> series, int maxPoints)
    {
        if (maxPoints <= 0)
        {
            return new HistoryQueryResult(resolution, series);
        }

        var (limited, downsampled) = HistoryDownsampler.Apply(series, maxPoints);
        return new HistoryQueryResult(resolution, limited, downsampled, maxPoints);
    }
}
