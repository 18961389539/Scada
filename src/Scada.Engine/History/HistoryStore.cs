using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data.Infrastructure;
using HistoryValueRow = Scada.Data.HistoryValue;

namespace Scada.Engine.History;

public sealed class HistoryStore : IHistoryStore, IGatewayHistoryWriter
{
    private readonly IHistoryDbFactory _factory;

    public HistoryStore(IHistoryDbFactory factory)
    {
        _factory = factory;
    }

    public async Task WriteRawAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return;
        }

        var distinct = values
            .GroupBy(x => (x.TagId, x.Ts))
            .Select(g => g.Last())
            .ToList();

        foreach (var monthGroup in distinct.GroupBy(x => MonthOf(x.Ts)))
        {
            await using var db = _factory.CreateRaw(monthGroup.Key.Year, monthGroup.Key.Month);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.HistoryValues.AddRange(monthGroup.Select(x => new HistoryValueRow
            {
                TagId = x.TagId,
                Ts = x.Ts,
                Value = x.Value,
                Quality = (int)x.Quality
            }));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task WriteGatewayHistoryAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken)
    {
        foreach (var month in values.GroupBy(x => MonthOf(x.Ts)))
        {
            await using var db = _factory.CreateRaw(month.Key.Year, month.Key.Month);
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL", cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await using var command = db.Database.GetDbConnection().CreateCommand(); command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "INSERT INTO HistoryValue (TagId,Ts,Value,Quality) VALUES ($tag,$ts,$value,$quality) ON CONFLICT(TagId,Ts) DO NOTHING";
            foreach (var name in new[] { "$tag", "$ts", "$value", "$quality" }) { var parameter = command.CreateParameter(); parameter.ParameterName = name; command.Parameters.Add(parameter); }
            foreach (var value in month)
            {
                command.Parameters[0].Value = value.TagId; command.Parameters[1].Value = value.Ts; command.Parameters[2].Value = value.Value; command.Parameters[3].Value = (int)value.Quality;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<HistorySeries>> QueryRawAsync(
        IReadOnlyCollection<int> tagIds,
        long from,
        long to,
        CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0 || to < from)
        {
            return Array.Empty<HistorySeries>();
        }

        var result = new Dictionary<int, HistorySeries>();
        foreach (var (year, month) in MonthsBetween(from, to))
        {
            if (!File.Exists(_factory.GetRawPath(year, month)))
            {
                continue;
            }

            await using var db = _factory.CreateRaw(year, month);
            var rows = await db.HistoryValues.AsNoTracking()
                .Where(x => tagIds.Contains(x.TagId) && x.Ts >= from && x.Ts <= to)
                .OrderBy(x => x.TagId).ThenBy(x => x.Ts)
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                AddPoint(result, row.TagId, new HistoryPoint(row.Ts, row.Value, (TagQuality)row.Quality));
            }
        }

        return result.Values.OrderBy(x => x.TagId).ToList();
    }

    public Task WriteAggregate5MinAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) =>
        WriteAggregatesAsync(values, aggregate5Min: true, cancellationToken);

    public Task WriteAggregateHourAsync(IReadOnlyCollection<AggregateValue> values, CancellationToken cancellationToken) =>
        WriteAggregatesAsync(values, aggregate5Min: false, cancellationToken);

    public async Task<IReadOnlyList<HistorySeries>> QueryAggregate5MinAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, HistorySeries>();
        foreach (var year in YearsBetween(from, to))
        {
            if (!File.Exists(_factory.GetAggregatePath(year)))
            {
                continue;
            }

            await using var db = _factory.CreateAggregate(year);
            var rows = await db.Agg5Min.AsNoTracking()
                .Where(x => tagIds.Contains(x.TagId) && x.Ts >= from && x.Ts <= to)
                .OrderBy(x => x.TagId).ThenBy(x => x.Ts)
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                AddPoint(result, row.TagId, new HistoryPoint(row.Ts, row.Avg, TagQuality.Good));
            }
        }

        return result.Values.OrderBy(x => x.TagId).ToList();
    }

    public async Task<IReadOnlyList<HistorySeries>> QueryAggregateHourAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, HistorySeries>();
        foreach (var year in YearsBetween(from, to))
        {
            if (!File.Exists(_factory.GetAggregatePath(year)))
            {
                continue;
            }

            await using var db = _factory.CreateAggregate(year);
            var rows = await db.AggHour.AsNoTracking()
                .Where(x => tagIds.Contains(x.TagId) && x.Ts >= from && x.Ts <= to)
                .OrderBy(x => x.TagId).ThenBy(x => x.Ts)
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                AddPoint(result, row.TagId, new HistoryPoint(row.Ts, row.Avg, TagQuality.Good));
            }
        }

        return result.Values.OrderBy(x => x.TagId).ToList();
    }

    public async Task<IReadOnlyList<AggregateValue>> QueryRawAggregatesAsync(long from, long to, CancellationToken cancellationToken)
    {
        var partials = new List<AggregateValue>();
        foreach (var (year, month) in MonthsBetween(from, to))
        {
            if (!File.Exists(_factory.GetRawPath(year, month)))
            {
                continue;
            }

            await using var db = _factory.CreateRaw(year, month);
            var rows = await db.HistoryValues.AsNoTracking()
                .Where(x => x.Ts >= from && x.Ts < to && x.Quality == (int)TagQuality.Good)
                .GroupBy(x => x.TagId)
                .Select(g => new
                {
                    TagId = g.Key,
                    Avg = g.Average(x => x.Value),
                    Min = g.Min(x => x.Value),
                    Max = g.Max(x => x.Value),
                    Count = g.Count()
                })
                .ToListAsync(cancellationToken);

            partials.AddRange(rows.Select(x => new AggregateValue(x.TagId, to, x.Avg, x.Min, x.Max, x.Count)));
        }

        return Combine(partials, AlignTo5Min(from));
    }

    public async Task<IReadOnlyList<AggregateValue>> Query5MinAggregatesAsync(long from, long to, CancellationToken cancellationToken)
    {
        var partials = new List<AggregateValue>();
        foreach (var year in YearsBetween(from, to))
        {
            if (!File.Exists(_factory.GetAggregatePath(year)))
            {
                continue;
            }

            await using var db = _factory.CreateAggregate(year);
            var rows = await db.Agg5Min.AsNoTracking()
                .Where(x => x.Ts >= from && x.Ts < to)
                .Select(x => new { x.TagId, x.Avg, x.Min, x.Max, x.Count })
                .ToListAsync(cancellationToken);

            partials.AddRange(rows.Select(x => new AggregateValue(x.TagId, from, x.Avg, x.Min, x.Max, x.Count)));
        }

        return Combine(partials, AlignToHour(from));
    }

    public async Task<IReadOnlyList<AggregatePoint>> QueryHourAggregatePointsAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken)
    {
        var points = new List<AggregatePoint>();
        foreach (var year in YearsBetween(from, to))
        {
            if (!File.Exists(_factory.GetAggregatePath(year)))
            {
                continue;
            }

            await using var db = _factory.CreateAggregate(year);
            var rows = await db.AggHour.AsNoTracking()
                .Where(x => tagIds.Contains(x.TagId) && x.Ts >= from && x.Ts <= to)
                .OrderBy(x => x.TagId).ThenBy(x => x.Ts)
                .Select(x => new AggregatePoint(x.TagId, x.Ts, x.Avg, x.Min, x.Max, x.Count))
                .ToListAsync(cancellationToken);
            points.AddRange(rows);
        }

        return points.OrderBy(x => x.TagId).ThenBy(x => x.Ts).ToList();
    }

    private async Task WriteAggregatesAsync(
        IReadOnlyCollection<AggregateValue> values,
        bool aggregate5Min,
        CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return;
        }

        var table = aggregate5Min ? "Agg5Min" : "AggHour";
        foreach (var yearGroup in values.GroupBy(x => DateTimeOffset.FromUnixTimeMilliseconds(x.Ts).UtcDateTime.Year))
        {
            await using var db = _factory.CreateAggregate(yearGroup.Key);
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"INSERT OR REPLACE INTO {table} (TagId, Ts, Avg, Min, Max, Count) VALUES ($tagId, $ts, $avg, $min, $max, $count)";
            var tagParam = command.CreateParameter();
            tagParam.ParameterName = "$tagId";
            command.Parameters.Add(tagParam);
            var tsParam = command.CreateParameter();
            tsParam.ParameterName = "$ts";
            command.Parameters.Add(tsParam);
            var avgParam = command.CreateParameter();
            avgParam.ParameterName = "$avg";
            command.Parameters.Add(avgParam);
            var minParam = command.CreateParameter();
            minParam.ParameterName = "$min";
            command.Parameters.Add(minParam);
            var maxParam = command.CreateParameter();
            maxParam.ParameterName = "$max";
            command.Parameters.Add(maxParam);
            var countParam = command.CreateParameter();
            countParam.ParameterName = "$count";
            command.Parameters.Add(countParam);

            foreach (var value in yearGroup)
            {
                tagParam.Value = value.TagId;
                tsParam.Value = value.Ts;
                avgParam.Value = value.Avg;
                minParam.Value = value.Min;
                maxParam.Value = value.Max;
                countParam.Value = value.Count;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static void AddPoint(Dictionary<int, HistorySeries> result, int tagId, HistoryPoint point)
    {
        if (!result.TryGetValue(tagId, out var series))
        {
            series = new HistorySeries { TagId = tagId };
            result[tagId] = series;
        }

        series.Points.Add(point);
    }

    private static IReadOnlyList<AggregateValue> Combine(List<AggregateValue> partials, long ts)
    {
        return partials
            .GroupBy(x => x.TagId)
            .Select(g =>
            {
                var totalCount = g.Sum(x => x.Count);
                var avg = totalCount == 0 ? 0 : g.Sum(x => x.Avg * x.Count) / totalCount;
                return new AggregateValue(g.Key, ts, avg, g.Min(x => x.Min), g.Max(x => x.Max), totalCount);
            })
            .ToList();
    }

    private static long AlignTo5Min(long ts)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(ts);
        var aligned = new DateTimeOffset(dto.Year, dto.Month, dto.Day, dto.Hour, dto.Minute / 5 * 5, 0, TimeSpan.Zero);
        return aligned.ToUnixTimeMilliseconds();
    }

    private static long AlignToHour(long ts)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(ts);
        var aligned = new DateTimeOffset(dto.Year, dto.Month, dto.Day, dto.Hour, 0, 0, TimeSpan.Zero);
        return aligned.ToUnixTimeMilliseconds();
    }

    private static (int Year, int Month) MonthOf(long ts)
    {
        var date = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;
        return (date.Year, date.Month);
    }

    private static IEnumerable<(int Year, int Month)> MonthsBetween(long from, long to)
    {
        var startDate = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime;
        var start = new DateTime(startDate.Year, startDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime;
        while (start <= end)
        {
            yield return (start.Year, start.Month);
            start = start.AddMonths(1);
        }
    }

    private static IEnumerable<int> YearsBetween(long from, long to)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime.Year;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime.Year;
        for (var year = start; year <= end; year++)
        {
            yield return year;
        }
    }
}
