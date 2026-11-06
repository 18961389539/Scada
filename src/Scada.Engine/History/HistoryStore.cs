using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data.Infrastructure;

namespace Scada.Engine.History;

public sealed class HistoryStore : IHistoryStore, IGatewayHistoryWriter, IHistoryStreamReader
{
    // 单条 INSERT 携带的行数：每行 4 个参数，200 行 800 个参数，低于 SQLite 的绑定上限，
    // 同时避免一个采集周期上千点时的往返次数（原先每点一次 ExecuteNonQuery）。
    private const int RowsPerInsert = 200;
    // 聚合行每行 7 个参数，100 行 700 个参数，同样留在绑定上限以内。
    private const int AggregateRowsPerInsert = 100;

    private readonly IHistoryDbFactory _factory;
    private readonly TimeZoneInfo _zone;
    // 三个字面量语句而不是拼接：PRAGMA 不支持参数化，传常量可以避免"用插值拼 SQL"的注入告警。
    private readonly string _synchronousPragma;

    public HistoryStore(IHistoryDbFactory factory, IOptions<ScadaSettings>? settings = null)
    {
        _factory = factory;
        _zone = HistoryCalendar.Resolve(settings?.Value.ShiftTimeZone);
        _synchronousPragma = settings?.Value.HistorySynchronous switch
        {
            HistorySyncMode.Full => "PRAGMA synchronous=FULL",
            HistorySyncMode.Off => "PRAGMA synchronous=OFF",
            _ => "PRAGMA synchronous=NORMAL"
        };
    }

    public async IAsyncEnumerable<HistoryValue> ReadAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0 || to < from) yield break;
        if (resolution == HistoryResolution.Raw)
        {
            foreach (var (year, month) in MonthsBetween(from, to))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(_factory.GetRawPath(year, month))) continue;
                await using var db = _factory.CreateRaw(year, month);
                var rows = db.HistoryValues.AsNoTracking()
                    .Where(x => tagIds.Contains(x.TagId) && x.Ts >= from && x.Ts <= to)
                    .OrderBy(x => x.TagId).ThenBy(x => x.Ts)
                    .Select(x => new HistoryValue(x.TagId, x.Ts, x.Value, (TagQuality)x.Quality));
                await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken)) yield return row;
            }
            yield break;
        }
        if (resolution is not (HistoryResolution.FiveMinute or HistoryResolution.Hour))
            throw new ArgumentOutOfRangeException(nameof(resolution));
        foreach (var year in YearsBetween(from, to))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(_factory.GetAggregatePath(year))) continue;
            await using var db = _factory.CreateAggregate(year);
            var rows = resolution == HistoryResolution.FiveMinute
                ? db.Agg5Min.AsNoTracking().Where(x => tagIds.Contains(x.TagId) && x.Ts >= from && x.Ts <= to)
                    .OrderBy(x => x.TagId).ThenBy(x => x.Ts).Select(x => new HistoryValue(x.TagId, x.Ts, x.Avg, x.BadCount > 0 ? TagQuality.Uncertain : TagQuality.Good))
                : db.AggHour.AsNoTracking().Where(x => tagIds.Contains(x.TagId) && x.Ts >= from && x.Ts <= to)
                    .OrderBy(x => x.TagId).ThenBy(x => x.Ts).Select(x => new HistoryValue(x.TagId, x.Ts, x.Avg, x.BadCount > 0 ? TagQuality.Uncertain : TagQuality.Good));
            await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(cancellationToken)) yield return row;
        }
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

        // A spool replay may include a month already committed before a crash.
        // Keep the original sample and continue inserting the rest of the batch.
        await WriteGatewayHistoryAsync(distinct, cancellationToken);
    }

    public async Task WriteGatewayHistoryAsync(IReadOnlyCollection<HistoryValue> values, CancellationToken cancellationToken)
    {
        foreach (var month in values.GroupBy(x => MonthOf(x.Ts)))
        {
            var rows = month as IReadOnlyList<HistoryValue> ?? month.ToArray();
            if (rows.Count == 0) continue;
            await using var db = _factory.CreateRaw(month.Key.Year, month.Key.Month);
            await db.Database.OpenConnectionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync(_synchronousPragma, cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var connection = db.Database.GetDbConnection();
            // 整月一次事务、分块多值 INSERT：原先每点一次 ExecuteNonQuery，万点周期是一万次语句往返。
            for (var offset = 0; offset < rows.Count; offset += RowsPerInsert)
            {
                var count = Math.Min(RowsPerInsert, rows.Count - offset);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = BuildHistoryInsert(count);
                for (var index = 0; index < count; index++)
                {
                    var value = rows[offset + index];
                    AddParameter(command, "$tag" + index, value.TagId);
                    AddParameter(command, "$ts" + index, value.Ts);
                    AddParameter(command, "$value" + index, value.Value);
                    AddParameter(command, "$quality" + index, (int)value.Quality);
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }

    // 更差的质量覆盖先到的点。质量相同保持先写入的值，重放不会改历史。更好的质量不能把坏点洗成好点。
    private static string BuildHistoryInsert(int rows)
    {
        var sql = new StringBuilder("INSERT INTO HistoryValue (TagId,Ts,Value,Quality) VALUES ");
        for (var index = 0; index < rows; index++)
        {
            if (index > 0) sql.Append(',');
            sql.Append("($tag").Append(index).Append(",$ts").Append(index).Append(",$value").Append(index)
               .Append(",$quality").Append(index).Append(')');
        }
        sql.Append(" ON CONFLICT(TagId,Ts) DO UPDATE SET Value = excluded.Value, Quality = excluded.Quality")
           .Append(" WHERE CASE excluded.Quality WHEN 2 THEN 3 WHEN 1 THEN 2 WHEN 3 THEN 1 ELSE 0 END")
           .Append(" > CASE HistoryValue.Quality WHEN 2 THEN 3 WHEN 1 THEN 2 WHEN 3 THEN 1 ELSE 0 END");
        return sql.ToString();
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
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
                AddPoint(result, row.TagId, new HistoryPoint(row.Ts, row.Avg, row.BadCount > 0 ? TagQuality.Uncertain : TagQuality.Good));
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
                AddPoint(result, row.TagId, new HistoryPoint(row.Ts, row.Avg, row.BadCount > 0 ? TagQuality.Uncertain : TagQuality.Good));
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
                .Where(x => x.Ts >= from && x.Ts < to)
                .GroupBy(x => x.TagId)
                .Select(g => new
                {
                    TagId = g.Key,
                    Avg = g.Where(x => x.Quality == (int)TagQuality.Good).Average(x => (double?)x.Value),
                    Min = g.Where(x => x.Quality == (int)TagQuality.Good).Min(x => (double?)x.Value),
                    Max = g.Where(x => x.Quality == (int)TagQuality.Good).Max(x => (double?)x.Value),
                    Count = g.Count(x => x.Quality == (int)TagQuality.Good),
                    BadCount = g.Count(x => x.Quality != (int)TagQuality.Good)
                })
                .ToListAsync(cancellationToken);

            partials.AddRange(rows.Where(x => x.Count > 0 && x.Avg is not null && x.Min is not null && x.Max is not null)
                .Select(x => new AggregateValue(x.TagId, to, x.Avg!.Value, x.Min!.Value, x.Max!.Value, x.Count, x.BadCount)));
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
                .Select(x => new { x.TagId, x.Avg, x.Min, x.Max, x.Count, x.BadCount })
                .ToListAsync(cancellationToken);

            partials.AddRange(rows.Select(x => new AggregateValue(x.TagId, from, x.Avg, x.Min, x.Max, x.Count, x.BadCount)));
        }

        return Combine(partials, AlignToHour(from));
    }

    public Task<IReadOnlyList<AggregatePoint>> QueryHourAggregatePointsAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, CancellationToken cancellationToken) =>
        QueryAggregatePointsAsync(tagIds, from, to, HistoryResolution.Hour, cancellationToken);

    public async Task<IReadOnlyList<AggregatePoint>> QueryAggregatePointsAsync(
        IReadOnlyCollection<int> tagIds, long from, long to, HistoryResolution resolution, CancellationToken cancellationToken)
    {
        if (resolution is not (HistoryResolution.FiveMinute or HistoryResolution.Hour))
            throw new ArgumentOutOfRangeException(nameof(resolution));
        var points = new List<AggregatePoint>();
        foreach (var year in YearsBetween(from, to))
        {
            if (!File.Exists(_factory.GetAggregatePath(year)))
            {
                continue;
            }

            await using var db = _factory.CreateAggregate(year);
            var rows = resolution == HistoryResolution.FiveMinute
                ? await db.Agg5Min.AsNoTracking()
                    .Where(x => tagIds.Contains(x.TagId) && x.Ts >= from && x.Ts <= to)
                    .OrderBy(x => x.TagId).ThenBy(x => x.Ts)
                    .Select(x => new AggregatePoint(x.TagId, x.Ts, x.Avg, x.Min, x.Max, x.Count))
                    .ToListAsync(cancellationToken)
                : await db.AggHour.AsNoTracking()
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
            var rows = yearGroup as IReadOnlyList<AggregateValue> ?? yearGroup.ToArray();
            await using var db = _factory.CreateAggregate(yearGroup.Key);
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            for (var offset = 0; offset < rows.Count; offset += AggregateRowsPerInsert)
            {
                var count = Math.Min(AggregateRowsPerInsert, rows.Count - offset);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = BuildAggregateInsert(table, count);
                for (var index = 0; index < count; index++)
                {
                    var value = rows[offset + index];
                    AddParameter(command, "$tagId" + index, value.TagId);
                    AddParameter(command, "$ts" + index, value.Ts);
                    AddParameter(command, "$avg" + index, value.Avg);
                    AddParameter(command, "$min" + index, value.Min);
                    AddParameter(command, "$max" + index, value.Max);
                    AddParameter(command, "$count" + index, value.Count);
                    AddParameter(command, "$bad" + index, value.BadCount);
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static string BuildAggregateInsert(string table, int rows)
    {
        var sql = new StringBuilder($"INSERT OR REPLACE INTO {table} (TagId, Ts, Avg, Min, Max, Count, BadCount) VALUES ");
        for (var index = 0; index < rows; index++)
        {
            if (index > 0) sql.Append(',');
            sql.Append("($tagId").Append(index).Append(",$ts").Append(index).Append(",$avg").Append(index)
               .Append(",$min").Append(index).Append(",$max").Append(index).Append(",$count").Append(index)
               .Append(",$bad").Append(index).Append(')');
        }
        return sql.ToString();
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
                return new AggregateValue(g.Key, ts, avg, g.Min(x => x.Min), g.Max(x => x.Max), totalCount, g.Sum(x => x.BadCount));
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

    private (int Year, int Month) MonthOf(long ts) => HistoryCalendar.WriteMonth(ts, _zone);

    private IEnumerable<(int Year, int Month)> MonthsBetween(long from, long to) => HistoryCalendar.MonthsOverlapping(from, to, _zone);

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
